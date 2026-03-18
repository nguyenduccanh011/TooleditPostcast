using System.Collections.Concurrent;
using System.Text.Json;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Executes the full AI processing pipeline:
/// 1. Normalize script (10%)
/// 2. Analyze script → AISegment[] with keywords (40%)
/// 3. Fetch ~60 image candidates per segment from 3 providers (70%)
/// 4. AI selects best image per segment (90%)
/// 5. Download chosen images → register as Assets (95%)
/// 6. Build text + visual Segment objects ready for DB (100%)
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
        CancellationToken ct = default)
    {
        Report(progress, 1, 5, "Chuẩn bị…");

        // ── Step 1: Normalize ──────────────────────────────────────────────
        Report(progress, 1, 10, "Đang chuẩn hóa script…");
        var normalizedScript = await _aiProvider.NormalizeScriptAsync(script, ct);

        // ── Step 2: Analyze → AISegment[] ─────────────────────────────────
        Report(progress, 2, 20, "AI đang phân tích script…");
        var analysisReq = new AIAnalysisRequest(normalizedScript, audioDuration);
        var analysisResp = await _aiProvider.AnalyzeScriptAsync(analysisReq, ct);
        var aiSegments  = analysisResp.Segments;
        Report(progress, 2, 40, $"Phân tích xong: {aiSegments.Length} segment");

        // ── Step 3+4: Fetch candidates + AI select ─────────────────────────
        Report(progress, 3, 50, "Đang tìm kiếm ảnh…");
        var (selectionResults, candidateUrlMap) = await _imageSelection.RunSelectBackgroundsAsync(aiSegments, progress, ct);
        Report(progress, 4, 90, "Đã chọn ảnh xong");

        // Build lookup: segmentIndex → chosen candidate ID
        var selectionMap = selectionResults.ToDictionary(r => r.SegmentIndex);

        // ── Step 5: Download images + register as assets ──────────────────
        Report(progress, 5, 90, "Đang tải ảnh về…");
        var textTrackId   = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Text)?.Id
                            ?? throw new InvalidOperationException("No text track in project");
        var visualTrackId = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Visual)?.Id
                            ?? throw new InvalidOperationException("No visual track in project");

        var registeredAssetIds = new List<string>();
        // segmentIndex → assetId
        var segmentAssetMap = new Dictionary<int, string>();

        // Download images in parallel (max 4 concurrent) instead of sequentially
        // to reduce total download time from O(n×latency) to O(ceil(n/4)×latency).
        var downloadResults = new ConcurrentDictionary<int, string>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, aiSegments.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, innerCt) =>
            {
                if (!selectionMap.TryGetValue(i, out var sel)) return;
                if (!candidateUrlMap.TryGetValue(sel.ChosenId, out var downloadUrl) || string.IsNullOrWhiteSpace(downloadUrl)) return;

                var assetId = await DownloadAndRegisterAsync(project.Id, sel.ChosenId, downloadUrl, innerCt);
                if (assetId != null)
                    downloadResults[i] = assetId;
            });

        foreach (var entry in downloadResults)
        {
            segmentAssetMap[entry.Key] = entry.Value;
            registeredAssetIds.Add(entry.Value);
        }

        Report(progress, 5, 95, $"Đã tải {registeredAssetIds.Count} ảnh");

        // ── Step 6: Build Segment objects ─────────────────────────────────
        Report(progress, 6, 98, "Xây dựng segment…");
        var textSegments   = new List<Segment>();
        var visualSegments = new List<Segment>();

        for (int i = 0; i < aiSegments.Length; i++)
        {
            var ai = aiSegments[i];
            var keywordsJson = JsonSerializer.Serialize(ai.Keywords);

            textSegments.Add(new Segment
            {
                ProjectId          = project.Id,
                TrackId            = textTrackId,
                StartTime          = Math.Round(ai.StartTime, 2),
                EndTime            = Math.Round(ai.EndTime,   2),
                Text               = ai.Text,
                Kind               = SegmentKind.Text,
                Keywords           = keywordsJson,
                TransitionType     = "fade",
                TransitionDuration = 0.5,
                Order              = i
            });

            var assetId = segmentAssetMap.TryGetValue(i, out var id) ? id : null;
            visualSegments.Add(new Segment
            {
                ProjectId          = project.Id,
                TrackId            = visualTrackId,
                StartTime          = Math.Round(ai.StartTime, 2),
                EndTime            = Math.Round(ai.EndTime,   2),
                Text               = ai.Text,
                Kind               = SegmentKind.Visual,
                Keywords           = keywordsJson,
                BackgroundAssetId  = assetId,
                TransitionType     = "fade",
                TransitionDuration = 0.5,
                Order              = i
            });
        }

        Report(progress, 6, 100, $"Hoàn thành! {textSegments.Count} segment, {registeredAssetIds.Count} ảnh");

        return new OrchestratorResult(
            [.. textSegments],
            [.. visualSegments],
            [.. registeredAssetIds]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> DownloadAndRegisterAsync(
        string projectId, string candidateId, string url, CancellationToken ct)
    {
        PreparedImageAsset? prepared = null;
        try
        {
            prepared = await _imageIngestService.DownloadAndPrepareAsync(url, candidateId, ct);
            var asset = await _projectService.AddAssetAsync(
                projectId,
                prepared.FilePath,
                "Image",
                prepared.Width,
                prepared.Height,
                prepared.FileSize);
            return asset.Id;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to download/register image {CandidateId}", candidateId);
            return null;
        }
        finally
        {
            if (prepared != null)
            {
                try { File.Delete(prepared.FilePath); } catch { /* best-effort */ }
            }
        }
    }

    private static void Report(IProgress<AIAnalysisProgressReport>? progress, int step, int percent, string msg)
        => progress?.Report(new AIAnalysisProgressReport(step, percent, msg));
}
