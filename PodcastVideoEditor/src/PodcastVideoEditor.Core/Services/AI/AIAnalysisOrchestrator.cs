using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Executes the full AI processing pipeline:
/// 1. Analyze script → AISegment[] with keywords + ASR correction (40%)
/// 2. Fetch ~60 image candidates per segment from 3 providers + AI select (90%)
/// 3. Download chosen images → register as Assets (95%)
/// 4. Build text + visual Segment objects ready for DB (100%)
/// </summary>
public sealed class AIAnalysisOrchestrator : IAIAnalysisOrchestrator
{
    private readonly IAIProvider _aiProvider;
    private readonly IAIImageSelectionService _imageSelection;
    private readonly ProjectService _projectService;
    private readonly ImageAssetIngestService _imageIngestService;

    public AIAnalysisOrchestrator(
        IAIProvider aiProvider,
        IAIImageSelectionService imageSelection,
        ProjectService projectService,
        ImageAssetIngestService imageIngestService)
    {
        _aiProvider     = aiProvider;
        _imageSelection = imageSelection;
        _projectService = projectService;
        _imageIngestService = imageIngestService;
    }

    public async Task<OrchestratorResult> RunAsync(
        Project project,
        string script,
        double? audioDuration = null,
        IProgress<AIAnalysisProgressReport>? progress = null,
        CancellationToken ct = default,
        bool closeGaps = true)
    {
        var totalSw = Stopwatch.StartNew();
        var segLineCount = PodcastVideoEditor.Core.Utilities.ScriptChunker.SplitIntoSegmentLines(script).Count;
        Log.Information("[AI-PIPELINE] ▶ START — project={ProjectId} transcriptLines={LineCount} audioDuration={Duration}s",
            project.Id, segLineCount, audioDuration?.ToString("F1") ?? "?");

        Report(progress, 1, 5, "Chuẩn bị…");

        // ── Step 1: Analyze → AISegment[] ─────────────────────────────────
        // AnalyzeScript prompt already includes ASR correction, so separate
        // NormalizeScript step is redundant and removed for speed.
        Report(progress, 1, 10, "AI đang phân tích script…");
        var sw1 = Stopwatch.StartNew();
        var analysisReq = new AIAnalysisRequest(script, audioDuration);
        var analysisResp = await _aiProvider.AnalyzeScriptAsync(analysisReq, ct);
        var aiSegments  = analysisResp.Segments;
        sw1.Stop();
        Log.Information("[AI-PIPELINE] ✔ Step1/AnalyzeScript — scenes={Scenes} fromLines={Lines} elapsed={Elapsed}s",
            aiSegments.Length, segLineCount, sw1.Elapsed.TotalSeconds.ToString("F1"));
        Report(progress, 1, 40, $"Phân tích xong: {aiSegments.Length} phân cảnh");

        // ── Step 2: Fetch candidates + AI select ───────────────────────
        Report(progress, 2, 50, "Đang tìm kiếm ảnh…");
        var sw2 = Stopwatch.StartNew();
        var (selectionResults, candidateUrlMap) = await _imageSelection.RunSelectBackgroundsAsync(aiSegments, progress, ct);
        sw2.Stop();
        Log.Information("[AI-PIPELINE] ✔ Step2/ImageSelect — selected={Selected}/{Total} elapsed={Elapsed}s",
            selectionResults.Length, aiSegments.Length, sw2.Elapsed.TotalSeconds.ToString("F1"));
        Report(progress, 2, 90, "Đã chọn ảnh xong");

        // Build lookup: segmentIndex → chosen candidate ID
        var selectionMap = selectionResults.ToDictionary(r => r.SegmentIndex);

        // ── Step 3: Download images + register as assets ──────────────
        Report(progress, 3, 90, "Đang tải ảnh về…");
        var textTrackId   = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Text)?.Id
                            ?? throw new InvalidOperationException("No text track in project");
        var visualTrackId = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Visual)?.Id
                            ?? throw new InvalidOperationException("No visual track in project");

        var registeredAssetIds = new List<string>();
        var segmentAssetMap = new Dictionary<int, string>();
        var preparedAssets = new ConcurrentDictionary<int, PreparedDownload>();
        var sw3 = Stopwatch.StartNew();
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, aiSegments.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (i, innerCt) =>
                {
                    if (!selectionMap.TryGetValue(i, out var sel)) return;
                    if (!candidateUrlMap.TryGetValue(sel.ChosenId, out var downloadUrl) || string.IsNullOrWhiteSpace(downloadUrl)) return;

                    var prepared = await DownloadAndPrepareAsync(sel.ChosenId, downloadUrl, innerCt);
                    if (prepared != null)
                        preparedAssets[i] = prepared;
                });

            // Register assets sequentially because ProjectService shares a single AppDbContext.
            // Concurrent SaveChanges on the same DbContext corrupts EF state tracking and throws
            // errors such as: "Unexpected entry.EntityState: Unchanged".
            foreach (var entry in preparedAssets.OrderBy(pair => pair.Key))
            {
                var assetId = await RegisterPreparedAssetAsync(project.Id, entry.Value, ct);
                if (assetId == null)
                    continue;

                segmentAssetMap[entry.Key] = assetId;
                registeredAssetIds.Add(assetId);
            }
        }
        finally
        {
            foreach (var prepared in preparedAssets.Values)
                TryDeleteFile(prepared.FilePath);
        }
        sw3.Stop();
        Log.Information("[AI-PIPELINE] ✔ Step3/DownloadImages — downloaded={Downloaded}/{Selected} elapsed={Elapsed}s",
            registeredAssetIds.Count, selectionResults.Length, sw3.Elapsed.TotalSeconds.ToString("F1"));

        Report(progress, 3, 95, $"Đã tải {registeredAssetIds.Count} ảnh");

        // ── Step 4: Build Segment objects ─────────────────────────────
        // Text segments: per-sentence from original script (subtitle-style)
        // Visual segments: merged scenes from AI analysis (1 image per scene)
        Report(progress, 4, 98, "Xây dựng segment…");
        var textSegments   = new List<Segment>();
        var visualSegments = new List<Segment>();

        // 4a: Text segments from original script lines (per-sentence)
        var scriptLines = PodcastVideoEditor.Core.Utilities.ScriptChunker.ParseTimestampedLines(script);
        for (int i = 0; i < scriptLines.Count; i++)
        {
            var (start, end, text) = scriptLines[i];
            textSegments.Add(new Segment
            {
                ProjectId          = project.Id,
                TrackId            = textTrackId,
                StartTime          = Math.Round(start, 2),
                EndTime            = Math.Round(end,   2),
                Text               = text,
                Kind               = SegmentKind.Text,
                TransitionType     = "fade",
                TransitionDuration = 0.5,
                Order              = i
            });
        }

        // 4b: Visual segments from AI scenes (merged, 10-15s each)
        for (int i = 0; i < aiSegments.Length; i++)
        {
            var ai = aiSegments[i];
            var assetId = segmentAssetMap.TryGetValue(i, out var id) ? id : null;
            visualSegments.Add(new Segment
            {
                ProjectId          = project.Id,
                TrackId            = visualTrackId,
                StartTime          = Math.Round(ai.StartTime, 2),
                EndTime            = Math.Round(ai.EndTime,   2),
                Text               = ai.Text,
                Kind               = SegmentKind.Visual,
                BackgroundAssetId  = assetId,
                TransitionType     = "fade",
                TransitionDuration = 0.5,
                Order              = i
            });
        }

        // ── Step 4c: Close gaps between segments ─────────────────────
        // ASR timestamps often have small gaps (e.g. 2.42→2.74). Extend each
        // segment's EndTime to the next segment's StartTime so scenes are seamless.
        // Close gaps independently since text and visual have different segment counts.
        if (closeGaps)
        {
            CloseGapsService.CloseGaps(textSegments);
            CloseGapsService.CloseGaps(visualSegments);
        }

        totalSw.Stop();
        Log.Information("[AI-PIPELINE] ■ DONE — segments={Segments} images={Images} total={Total}s  " +
            "(analyze={A}s  imageSelect={B}s  download={C}s)",
            textSegments.Count, registeredAssetIds.Count,
            totalSw.Elapsed.TotalSeconds.ToString("F1"),
            sw1.Elapsed.TotalSeconds.ToString("F1"),
            sw2.Elapsed.TotalSeconds.ToString("F1"),
            sw3.Elapsed.TotalSeconds.ToString("F1"));

        Report(progress, 4, 100, $"Hoàn thành! {textSegments.Count} segment, {registeredAssetIds.Count} ảnh");

        return new OrchestratorResult(
            [.. textSegments],
            [.. visualSegments],
            [.. registeredAssetIds]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<PreparedDownload?> DownloadAndPrepareAsync(
        string candidateId, string url, CancellationToken ct)
    {
        try
        {
            var prepared = await _imageIngestService.DownloadAndPrepareAsync(url, candidateId, ct);
            return new PreparedDownload(candidateId, prepared.FilePath, prepared.Width, prepared.Height, prepared.FileSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to download/prepare image {CandidateId}", candidateId);
            return null;
        }
    }

    private async Task<string?> RegisterPreparedAssetAsync(
        string projectId,
        PreparedDownload prepared,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var asset = await _projectService.AddAssetAsync(
                projectId,
                prepared.FilePath,
                "Image");
            return asset.Id;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register image {CandidateId}", prepared.CandidateId);
            return null;
        }
        finally
        {
            TryDeleteFile(prepared.FilePath);
        }
    }

    private static void Report(IProgress<AIAnalysisProgressReport>? progress, int step, int percent, string msg)
        => progress?.Report(new AIAnalysisProgressReport(step, percent, msg));

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private sealed record PreparedDownload(
        string CandidateId,
        string FilePath,
        int Width,
        int Height,
        long FileSize);
}
