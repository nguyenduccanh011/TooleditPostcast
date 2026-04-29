#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// ViewModel for the CapCut export dialog. Maps the current project's tracks/segments
/// to CapCut API calls and manages the export progress.
/// </summary>
public partial class CapCutExportViewModel : ObservableObject, IDisposable
{
    private readonly CapCutExportService _exportService;
    private readonly TimelineViewModel _timelineViewModel;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    [ObservableProperty] private string _draftName = "PodcastExport";
    [ObservableProperty] private int _exportWidth = 1080;
    [ObservableProperty] private int _exportHeight = 1920;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string _statusMessage = "Ready to export to CapCut";
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _exportCompleted;
    [ObservableProperty] private string _draftFolder = string.Empty;
    [ObservableProperty] private bool _renderVisualizerAsVideo = true;

    public Project? Project { get; set; }

    public CapCutExportViewModel(CapCutExportService exportService, TimelineViewModel timelineViewModel)
    {
        _exportService = exportService;
        _timelineViewModel = timelineViewModel;
    }

    public void AttachTimeline(TimelineViewModel timeline) { /* already via ctor */ }

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private async Task ExportToCapCutAsync()
    {
        if (Project is null) return;

        IsExporting = true;
        HasError = false;
        ExportCompleted = false;
        ExportProgress = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // 1. Start server
            StatusMessage = "Starting CapCut API server...";
            ExportProgress = 5;
            var serverOk = await _exportService.EnsureServerRunningAsync(ct);
            if (!serverOk)
            {
                var detail = _exportService.LastError ?? "Unknown";
                var dir = _exportService.ServerDirectory;
                StatusMessage = $"Cannot start CapCut API server.\nServerDir: {dir}\nDetail: {detail}";
                HasError = true;
                return;
            }

            // 2. Create draft
            StatusMessage = "Creating CapCut draft...";
            ExportProgress = 10;

            var width = ExportWidth;
            var height = ExportHeight;
            var (sourceCanvasWidth, sourceCanvasHeight) = ResolveSourceCanvasSize(Project, width, height);
            var renderFrameRate = 30;
            var sourceCanvasWidthForBake = sourceCanvasWidth > 0 ? sourceCanvasWidth : width;
            var sourceCanvasHeightForBake = sourceCanvasHeight > 0 ? sourceCanvasHeight : height;

            var draftResult = await _exportService.CreateDraftAsync(
                string.IsNullOrWhiteSpace(DraftName) ? Project.Name ?? "PodcastExport" : DraftName,
                width, height, ct);

            if (draftResult is null)
            {
                StatusMessage = "Cannot create CapCut draft.";
                HasError = true;
                return;
            }

            // Extract draft_id from response: {"success": true, "output": {"draft_id": "...", "draft_url": "..."}}
            string draftId = string.Empty;
            if (draftResult.RootElement.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("draft_id", out var did))
            {
                draftId = did.GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(draftId))
            {
                var rawJson = draftResult.RootElement.GetRawText();
                Log.Warning("create_draft response: {Json}", rawJson);
                StatusMessage = $"Cannot get draft_id from API. Response: {rawJson}";
                HasError = true;
                return;
            }

            DraftFolder = draftId;
            Log.Information("Created CapCut draft: {DraftId}", draftId);

            // 3. Map tracks and segments
            var tracks = Project.Tracks?.ToList() ?? [];
            var totalSegments = tracks.Sum(t => t.Segments?.Count ?? 0);
            var processedSegments = 0;
            var lastEndByTrack = new Dictionary<string, double>(StringComparer.Ordinal);
            var subtitleGroupIdByTrack = new Dictionary<string, string>(StringComparer.Ordinal);
            const double MinCapCutDuration = 0.05;
            string? visualizerAudioPath = null;
            string? ffmpegPath = null;
            var hasEffectTrack = tracks.Any(t => string.Equals(t.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase));
            var effectTrackCount = tracks.Count(t => string.Equals(t.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase));
            var effectSegmentCount = tracks
                .Where(t => string.Equals(t.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase))
                .Sum(t => t.Segments?.Count ?? 0);
            var visualizerElementCount = (Project.Elements ?? Enumerable.Empty<Element>())
                .Count(e => e.IsVisible && string.Equals(e.Type, nameof(ElementType.Visualizer), StringComparison.OrdinalIgnoreCase));
            Log.Information(
                "CapCut export diagnostics: tracks={TrackCount}, totalSegments={TotalSegments}, effectTracks={EffectTrackCount}, effectSegments={EffectSegmentCount}, visualizerElements={VisualizerElementCount}, renderVisualizerAsVideo={RenderVisualizerAsVideo}",
                tracks.Count,
                totalSegments,
                effectTrackCount,
                effectSegmentCount,
                visualizerElementCount,
                RenderVisualizerAsVideo);

            // Build a lookup of canvas elements by SegmentId for position/size mapping
            var elementsBySegment = (Project.Elements ?? Enumerable.Empty<Element>())
                .Where(e => e.SegmentId != null && e.IsVisible)
                .GroupBy(e => e.SegmentId!)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            if (RenderVisualizerAsVideo && hasEffectTrack)
            {
                StatusMessage = "Preparing visualizer export...";
                if (!FFmpegService.IsInitialized())
                {
                    var ffmpegInit = await FFmpegService.InitializeAsync();
                    if (!ffmpegInit.IsValid)
                    {
                        StatusMessage = "FFmpeg not found. Visualizer export to CapCut requires FFmpeg.";
                        HasError = true;
                        return;
                    }
                }

                ffmpegPath = FFmpegService.GetFFmpegPath();
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    StatusMessage = "Cannot resolve FFmpeg path for visualizer export.";
                    HasError = true;
                    return;
                }

                visualizerAudioPath = RenderSegmentBuilder.ResolveProjectAudioPath(Project);
                if (string.IsNullOrWhiteSpace(visualizerAudioPath) || !File.Exists(visualizerAudioPath))
                {
                    Log.Warning(
                        "CapCut export visualizer diagnostics: audio source missing. ResolvedPath='{AudioPath}'",
                        visualizerAudioPath ?? "(null)");
                    StatusMessage = "No audio source found for visualizer bake.";
                    HasError = true;
                    return;
                }
                Log.Information(
                    "CapCut export visualizer diagnostics: ffmpeg='{FfmpegPath}', audio='{AudioPath}'",
                    ffmpegPath,
                    visualizerAudioPath);
            }

            // Compute max order among canvas-visible tracks so we can invert Order → relative_index.
            // In our editor: lower Order = foreground (top). In CapCut: higher render_index = foreground.
            var canvasTracks = tracks
                .Where(t => string.Equals(t.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var maxCanvasOrder = canvasTracks.Count > 0 ? canvasTracks.Max(t => t.Order) : 0;
            const int visualLayerStep = 10;

            foreach (var track in tracks.OrderBy(t => t.Order))
            {
                var segments = track.Segments?.OrderBy(s => s.StartTime).ToList() ?? [];
                var trackKey = GetTrackExportKey(track);
                // Compute relative_index for canvas-visible tracks: invert Order so top track gets highest render_index
                var isCanvasTrack = string.Equals(track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(track.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase);
                var trackRelativeIndex = isCanvasTrack
                    ? (maxCanvasOrder - track.Order) * visualLayerStep
                    : 0;

                if (string.Equals(track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase))
                {
                    var isLayoutTrack = !string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
                        && !string.Equals(track.ImageLayoutPreset, ImageLayoutPresets.FullFrame, StringComparison.OrdinalIgnoreCase);
                    if (isLayoutTrack)
                    {
                        await AddLayoutShapeBarsForTrackAsync(
                            draftId,
                            track,
                            segments,
                            width,
                            height,
                            trackRelativeIndex,
                            ct);
                    }
                }

                // Pre-compute a fallback element for this track: if a segment has no canvas
                // element of its own, use the first element found among any sibling segment
                // on the same track. This handles the common case where the editor only creates
                // one canvas element per track (for the segment that was active at the playhead).
                Element? trackFallbackElement = null;
                foreach (var s in segments)
                {
                    if (elementsBySegment.TryGetValue(s.Id, out var els) && els.Count > 0)
                    {
                        trackFallbackElement = els[0];
                        break;
                    }
                }

                foreach (var seg in segments)
                {
                    ct.ThrowIfCancellationRequested();

                    var progressPct = 15 + (int)(75.0 * processedSegments / Math.Max(totalSegments, 1));
                    ExportProgress = progressPct;

                    var start = seg.StartTime;
                    var end = seg.EndTime;
                    var duration = end - start;

                    // Look up canvas element for this segment to get position/size
                    elementsBySegment.TryGetValue(seg.Id, out var segElements);

                    switch (track.TrackType?.ToLowerInvariant())
                    {
                        case "visual":
                            var assetPath = ResolveAssetPath(seg);
                            if (string.IsNullOrEmpty(assetPath))
                            {
                                Log.Warning(
                                    "CapCut export skipped visual segment {SegmentId} in track '{TrackName}' because asset path was not resolved (assetId={AssetId})",
                                    seg.Id, track.Name, seg.BackgroundAssetId);
                                break;
                            }

                            // CapCut API rejects overlap in the same track.
                            // Clamp start time to avoid silent drops in dense AI-content tracks.
                            var targetStart = start;
                            if (lastEndByTrack.TryGetValue(trackKey, out var lastEnd) && targetStart < lastEnd)
                                targetStart = lastEnd;
                            var targetDuration = Math.Max(MinCapCutDuration, end - targetStart);
                            lastEndByTrack[trackKey] = targetStart + targetDuration;

                            // Compute CapCut transform from canvas element position
                            // Fall back to the track-level element if this segment has none.
                            // For visual tracks, each segment may have its own unique position —
                            // do NOT fall back to a sibling segment's element.
                            var visualEl = segElements?.FirstOrDefault(e =>
                                e.Type is "Image" or "Logo");
                            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
                            var forceUniformScale = ext is not ".mp4" and not ".mov" and not ".avi" and not ".mkv" and not ".webm";
                            var isStillImage = forceUniformScale;
                            var isLogoElement = string.Equals(visualEl?.Type, "Logo", StringComparison.OrdinalIgnoreCase);
                            var shouldApplyImageLayoutMask = isStillImage && !isLogoElement;
                            var (vTx, vTy, vSx, vSy) = ComputeCapCutTransform(
                                visualEl,
                                sourceCanvasWidth,
                                sourceCanvasHeight,
                                forceUniformScale);
                            if (shouldApplyImageLayoutMask)
                            {
                                var coverScale = ComputeImageLayoutCoverScaleMultiplier(
                                    track,
                                    seg,
                                    width,
                                    height,
                                    vSx,
                                    vSy,
                                    Project);
                                if (coverScale > 1.0)
                                {
                                    vSx *= coverScale;
                                    vSy *= coverScale;
                                }
                            }

                            if (ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm")
                            {
                                StatusMessage = $"Adding video: {Path.GetFileName(assetPath)}";
                                var addVideoResult = await _exportService.AddVideoAsync(
                                    draftId,
                                    assetPath,
                                    seg.SourceStartOffset,
                                    seg.SourceStartOffset + targetDuration,
                                    width,
                                    height,
                                    targetStart,
                                    trackKey,
                                    volume: seg.Volume,
                                    transformX: vTx,
                                    transformY: vTy,
                                    scaleX: vSx,
                                    scaleY: vSy,
                                    relativeIndex: trackRelativeIndex,
                                    ct: ct);
                                EnsureApiSuccess(addVideoResult, $"add_video failed for track '{trackKey}', segment {seg.Id}");
                            }
                            else
                            {
                                StatusMessage = $"Adding image: {Path.GetFileName(assetPath)}";
                                var imageMask = BuildImageMaskFromTrackLayout(track, width, height);
                                var addImageResult = await _exportService.AddImageAsync(
                                    draftId,
                                    assetPath,
                                    targetStart,
                                    targetDuration,
                                    width,
                                    height,
                                    trackKey,
                                    transformX: vTx,
                                    transformY: vTy,
                                    scaleX: vSx,
                                    scaleY: vSy,
                                    maskType: shouldApplyImageLayoutMask ? imageMask?.MaskType : null,
                                    maskCenterX: shouldApplyImageLayoutMask ? imageMask?.CenterX ?? 0 : 0,
                                    maskCenterY: shouldApplyImageLayoutMask ? imageMask?.CenterY ?? 0 : 0,
                                    maskSize: shouldApplyImageLayoutMask ? imageMask?.Size ?? 0.5 : 0.5,
                                    maskRectWidth: shouldApplyImageLayoutMask ? imageMask?.RectWidth : null,
                                    relativeIndex: trackRelativeIndex,
                                    ct: ct);
                                EnsureApiSuccess(addImageResult, $"add_image failed for track '{trackKey}', segment {seg.Id}");
                            }
                            break;

                        case "audio":
                            var audioPath = ResolveAssetPath(seg);
                            if (!string.IsNullOrEmpty(audioPath))
                            {
                                StatusMessage = $"Adding audio: {Path.GetFileName(audioPath)}";
                                var addAudioResult = await _exportService.AddAudioAsync(
                                    draftId,
                                    audioPath,
                                    seg.SourceStartOffset,
                                    seg.SourceStartOffset + duration,
                                    start,
                                    track.Name ?? $"audio_{track.Order}",
                                    seg.Volume,
                                    seg.FadeInDuration,
                                    seg.FadeOutDuration,
                                    ct);
                                EnsureApiSuccess(addAudioResult, $"add_audio failed for track '{track.Name}', segment {seg.Id}");
                            }
                            break;

                        case "text":
                            if (!string.IsNullOrEmpty(seg.Text))
                            {
                                var isScriptTextTrack = string.Equals(
                                    track.TrackRole,
                                    TrackRoles.ScriptText,
                                    StringComparison.OrdinalIgnoreCase);

                                var subtitleGroupId = string.Empty;
                                if (isScriptTextTrack && !subtitleGroupIdByTrack.TryGetValue(trackKey, out subtitleGroupId))
                                {
                                    subtitleGroupId = $"vi-VN_{Guid.NewGuid():N}";
                                    subtitleGroupIdByTrack[trackKey] = subtitleGroupId;
                                }

                                // Look up text canvas element for position/size.
                                // Fall back to the track-level element if this segment has none.
                                var textEl = segElements?.FirstOrDefault(e => e.Type == "TextOverlay")
                                    ?? (trackFallbackElement?.Type == "TextOverlay" ? trackFallbackElement : null);
                                var (tTx, tTy, _, _) = ComputeCapCutTransform(textEl, sourceCanvasWidth, sourceCanvasHeight);
                                var (fixedWidthRatio, fixedHeightRatio) = ComputeCapCutTextBounds(
                                    textEl,
                                    sourceCanvasWidth,
                                    sourceCanvasHeight);
                                var trackStyle = track.TextStyle;
                                var sourceFontSize = TryGetTextElementFontSize(textEl) ?? trackStyle?.FontSize;
                                var exportFontSize = ConvertEditorFontSizeToCapCut(sourceFontSize);
                                var sourceTextStyle = TryGetTextElementStyleFlags(textEl);
                                var exportIsBold = sourceTextStyle?.IsBold ?? trackStyle?.IsBold ?? false;
                                var exportIsItalic = sourceTextStyle?.IsItalic ?? trackStyle?.IsItalic ?? false;
                                var exportIsUnderline = sourceTextStyle?.IsUnderline ?? trackStyle?.IsUnderline ?? false;
                                var exportColor = string.IsNullOrWhiteSpace(trackStyle?.ColorHex) ? "#FFFFFF" : trackStyle!.ColorHex;
                                var exportFontFamily = string.IsNullOrWhiteSpace(trackStyle?.FontFamily) ? null : trackStyle!.FontFamily;
                                var wrappedText = PrepareCapCutTextWithWordWrap(
                                    seg.Text,
                                    fixedWidthRatio,
                                    exportFontSize,
                                    width);

                                StatusMessage = $"Adding text: {seg.Text[..Math.Min(30, seg.Text.Length)]}...";

                                var addTextResult = await _exportService.AddTextAsync(
                                    draftId,
                                    wrappedText,
                                    start,
                                    duration,
                                    track.Name ?? $"text_{track.Order}",
                                    fontFamily: exportFontFamily,
                                    fontSize: exportFontSize,
                                    color: exportColor,
                                    transformX: tTx,
                                    transformY: tTy,
                                    isBold: exportIsBold,
                                    isItalic: exportIsItalic,
                                    isUnderline: exportIsUnderline,
                                    isSubtitle: isScriptTextTrack,
                                    subtitleGroupId: subtitleGroupId,
                                    fixedWidth: fixedWidthRatio,
                                    fixedHeight: fixedHeightRatio,
                                    ct: ct);
                                EnsureApiSuccess(addTextResult, $"add_text failed for track '{track.Name}', segment {seg.Id}");
                            }
                            break;

                        case "effect":
                            if (!RenderVisualizerAsVideo)
                            {
                                Log.Information("Skipping effect track '{Track}' because RenderVisualizerAsVideo=false", track.Name);
                                StatusMessage = $"Skipping effect track: {track.Name}";
                                break;
                            }

                            if (string.IsNullOrWhiteSpace(visualizerAudioPath) || string.IsNullOrWhiteSpace(ffmpegPath))
                            {
                                Log.Warning("Skipping effect segment {SegmentId} because visualizer prerequisites are missing", seg.Id);
                                break;
                            }

                            var visualizerDbElement = segElements?.FirstOrDefault(e =>
                                string.Equals(e.Type, nameof(ElementType.Visualizer), StringComparison.OrdinalIgnoreCase))
                                ?? (trackFallbackElement is not null
                                    && string.Equals(trackFallbackElement.Type, nameof(ElementType.Visualizer), StringComparison.OrdinalIgnoreCase)
                                    ? trackFallbackElement
                                    : null);
                            if (visualizerDbElement is null)
                            {
                                Log.Information(
                                    "Skipping effect segment {SegmentId} on track '{TrackName}' because no Visualizer element was found (segmentElementCount={SegmentElementCount}, trackFallbackType={TrackFallbackType})",
                                    seg.Id,
                                    track.Name,
                                    segElements?.Count ?? 0,
                                    trackFallbackElement?.Type ?? "(null)");
                                break;
                            }

                            if (ElementMapper.ToCanvasElement(visualizerDbElement) is not VisualizerElement visualizerElement)
                            {
                                Log.Warning("Skipping effect segment {SegmentId}: failed to map element {ElementId} to VisualizerElement", seg.Id, visualizerDbElement.Id);
                                break;
                            }

                            var effectTargetStart = start;
                            if (lastEndByTrack.TryGetValue(trackKey, out var effectLastEnd) && effectTargetStart < effectLastEnd)
                                effectTargetStart = effectLastEnd;
                            var effectTargetDuration = Math.Max(MinCapCutDuration, end - effectTargetStart);
                            lastEndByTrack[trackKey] = effectTargetStart + effectTargetDuration;

                            StatusMessage = $"Baking visualizer: {track.Name}";
                            var bakedVisualizerPath = await OfflineVisualizerBaker.BakeAsync(
                                visualizerElement,
                                visualizerAudioPath,
                                width,
                                height,
                                sourceCanvasWidthForBake,
                                sourceCanvasHeightForBake,
                                effectTargetStart,
                                effectTargetStart + effectTargetDuration,
                                renderFrameRate,
                                ffmpegPath,
                                progress: null,
                                ct);

                            if (string.IsNullOrWhiteSpace(bakedVisualizerPath))
                            {
                                Log.Warning("Visualizer bake returned empty path for segment {SegmentId} on track '{TrackName}'", seg.Id, track.Name);
                                break;
                            }
                            Log.Information(
                                "Visualizer bake completed for segment {SegmentId}: path='{Path}', start={Start:F3}, duration={Duration:F3}",
                                seg.Id,
                                bakedVisualizerPath,
                                effectTargetStart,
                                effectTargetDuration);

                            var (vizTx, vizTy, vizSx, vizSy) = ComputeCapCutTransform(
                                visualizerDbElement,
                                sourceCanvasWidth,
                                sourceCanvasHeight,
                                forceUniformScale: false);

                            StatusMessage = $"Adding visualizer video: {Path.GetFileName(bakedVisualizerPath)}";
                            var addVisualizerVideoResult = await _exportService.AddVideoAsync(
                                draftId,
                                bakedVisualizerPath,
                                0,
                                effectTargetDuration,
                                width,
                                height,
                                effectTargetStart,
                                trackKey,
                                volume: 0,
                                transformX: vizTx,
                                transformY: vizTy,
                                scaleX: vizSx,
                                scaleY: vizSy,
                                relativeIndex: trackRelativeIndex,
                                ct: ct);
                            if (addVisualizerVideoResult is not null)
                            {
                                Log.Information(
                                    "CapCut visualizer add_video response for segment {SegmentId}: {Json}",
                                    seg.Id,
                                    addVisualizerVideoResult.RootElement.GetRawText());
                            }
                            EnsureApiSuccess(addVisualizerVideoResult, $"add_video failed for visualizer track '{trackKey}', segment {seg.Id}");
                            break;
                    }

                    processedSegments++;
                }
            }

            // 4. Save draft
            StatusMessage = "Saving CapCut draft...";
            ExportProgress = 92;
            var saveResult = await _exportService.SaveDraftAsync(draftId, ct);
            if (saveResult is null)
            {
                StatusMessage = "Export completed but failed to save draft into CapCut.";
                HasError = true;
                return;
            }
            Log.Information("CapCut save_draft response for {DraftId}: {Json}", draftId, saveResult.RootElement.GetRawText());
            EnsureApiSuccess(saveResult, "save_draft failed");

            ExportProgress = 100;
            ExportCompleted = true;
            StatusMessage = $"Export success. Draft saved to CapCut.\nDraft ID: {draftId}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export canceled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CapCut export failed");
            StatusMessage = $"Export error: {ex.Message}";
            HasError = true;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanStartExport() => !IsExporting;

    [RelayCommand]
    private void CancelExport()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenDraftFolder()
    {
        if (!string.IsNullOrEmpty(DraftFolder) && Directory.Exists(DraftFolder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DraftFolder,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Resolves the file path for a segment's asset.
    /// </summary>
    private string? ResolveAssetPath(Segment seg)
    {
        if (seg.BackgroundAssetId is null || Project?.Assets is null) return null;
        var asset = Project.Assets.FirstOrDefault(a => a.Id == seg.BackgroundAssetId);
        return asset?.FilePath;
    }

    /// <summary>
    /// Converts a canvas element's pixel position/size to CapCut's coordinate system.
    /// CapCut uses: transform_x/y in half-canvas-dimension units (0=center, ±1=edge),
    /// scale_x/y as fraction of canvas size (1.0=full canvas).
    /// Returns (transformX, transformY, scaleX, scaleY). If element is null, returns defaults (0,0,1,1).
    /// </summary>
    private static (double transformX, double transformY, double scaleX, double scaleY)
        ComputeCapCutTransform(
            Element? element,
            int canvasWidth,
            int canvasHeight,
            bool forceUniformScale = false)
    {
        if (element is null || canvasWidth <= 0 || canvasHeight <= 0)
            return (0, 0, 1, 1);

        // Element center in pixel coords
        double elemCenterX = element.X + element.Width / 2.0;
        double elemCenterY = element.Y + element.Height / 2.0;

        // Canvas center
        double canvasCenterX = canvasWidth / 2.0;
        double canvasCenterY = canvasHeight / 2.0;

        // CapCut transform: offset from center in half-canvas units
        // Note: CapCut Y-axis is inverted (negative = down, positive = up) compared to
        // our canvas where Y increases downward, so we negate the Y offset.
        double transformX = (elemCenterX - canvasCenterX) / canvasCenterX;
        double transformY = -((elemCenterY - canvasCenterY) / canvasCenterY);

        // CapCut scale: element size as fraction of canvas
        double scaleX = element.Width / canvasWidth;
        double scaleY = element.Height / canvasHeight;

        if (forceUniformScale)
        {
            // CapCut image overlays expect uniform clip scale for correct aspect.
            // Keep width-derived scale as source of truth (matches editor behavior better).
            scaleY = scaleX;
        }

        return (transformX, transformY, scaleX, scaleY);
    }

    private static (int width, int height) ResolveSourceCanvasSize(Project project, int fallbackWidth, int fallbackHeight)
    {
        var aspect = project.RenderSettings?.AspectRatio;
        if (!string.IsNullOrWhiteSpace(aspect))
        {
            var (previewWidth, previewHeight) = RenderSizing.ResolvePreviewSize(aspect, previewShortEdge: 1080);
            var w = (int)Math.Round(previewWidth);
            var h = (int)Math.Round(previewHeight);
            if (w > 0 && h > 0)
                return (w, h);
        }

        return (fallbackWidth, fallbackHeight);
    }

    private sealed record ImageMaskConfig(
        string MaskType,
        double CenterX,
        double CenterY,
        double Size,
        double? RectWidth);

    private static double ComputeImageLayoutCoverScaleMultiplier(
        Track track,
        Segment segment,
        int frameWidth,
        int frameHeight,
        double currentScaleX,
        double currentScaleY,
        Project? project)
    {
        if (project?.Assets is null || frameWidth <= 0 || frameHeight <= 0)
            return 1.0;

        var preset = string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
            ? ImageLayoutPresets.FullFrame
            : track.ImageLayoutPreset;

        if (string.Equals(preset, ImageLayoutPresets.FullFrame, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var asset = project.Assets.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
        var (assetWidth, assetHeight) = ResolveAssetDimensions(asset);
        if (assetWidth <= 0 || assetHeight <= 0)
            return 1.0;

        var (_, _, _, layoutHeightPx) = RenderHelper.ComputeImageRect(preset, frameWidth, frameHeight);
        if (layoutHeightPx <= 0)
            return 1.0;

        // Uniform scale is required for images in our exporter; use max as the current effective uniform.
        var uniformScale = Math.Max(currentScaleX, currentScaleY);
        if (uniformScale <= 0)
            return 1.0;

        // Approximate visible image height ratio in frame at current scale.
        // scale=1 corresponds to full-frame width fit, so height ratio scales with aspect.
        var visibleHeightRatio = uniformScale * (assetHeight / (double)assetWidth);
        var targetHeightRatio = layoutHeightPx / frameHeight;

        if (visibleHeightRatio <= 0 || targetHeightRatio <= visibleHeightRatio)
            return 1.0;

        return Math.Clamp(targetHeightRatio / visibleHeightRatio, 1.0, 8.0);
    }

    private static (int width, int height) ResolveAssetDimensions(Asset? asset)
    {
        if (asset?.Width is > 0 && asset.Height is > 0)
            return (asset.Width.Value, asset.Height.Value);

        if (asset is null || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
            return (0, 0);

        try
        {
            using var fs = File.OpenRead(asset.FilePath);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null)
                return (0, 0);

            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static ImageMaskConfig? BuildImageMaskFromTrackLayout(Track track, int frameWidth, int frameHeight)
    {
        var preset = string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
            ? ImageLayoutPresets.FullFrame
            : track.ImageLayoutPreset;

        if (string.Equals(preset, ImageLayoutPresets.FullFrame, StringComparison.OrdinalIgnoreCase))
            return null;

        if (frameWidth <= 0 || frameHeight <= 0)
            return null;

        var (_, _, rectW, rectH) = RenderHelper.ComputeImageRect(preset, frameWidth, frameHeight);
        if (rectW <= 0 || rectH <= 0)
            return null;

        return new ImageMaskConfig(
            MaskType: "Rectangle",
            CenterX: 0.0,
            CenterY: 0.0,
            Size: Math.Clamp(rectH / frameHeight, 0.01, 1.0),
            RectWidth: Math.Clamp(rectW / frameWidth, 0.01, 1.0));
    }

    private async Task AddLayoutShapeBarsForTrackAsync(
        string draftId,
        Track track,
        List<Segment> segments,
        int frameWidth,
        int frameHeight,
        int trackRelativeIndex,
        CancellationToken ct)
    {
        if (segments.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
            return;

        var preset = string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
            ? ImageLayoutPresets.FullFrame
            : track.ImageLayoutPreset;
        if (string.Equals(preset, ImageLayoutPresets.FullFrame, StringComparison.OrdinalIgnoreCase))
            return;

        var (_, _, _, rectH) = RenderHelper.ComputeImageRect(preset, frameWidth, frameHeight);
        var barHeight = (int)Math.Round((frameHeight - rectH) / 2.0);
        if (barHeight <= 0)
            return;

        var imageSegments = segments
            .Where(s =>
            {
                var path = ResolveAssetPath(s);
                if (string.IsNullOrWhiteSpace(path))
                    return false;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                return ext is not ".mp4" and not ".mov" and not ".avi" and not ".mkv" and not ".webm";
            })
            .OrderBy(s => s.StartTime)
            .ToList();
        if (imageSegments.Count == 0)
            return;

        var start = imageSegments.First().StartTime;
        var end = imageSegments.Max(s => s.EndTime);
        var duration = Math.Max(0.05, end - start);
        if (duration <= 0)
            return;

        var topTransformY = 1.0 - (barHeight / (double)frameHeight);
        var bottomTransformY = -topTransformY;
        var overlayRelativeIndex = trackRelativeIndex + 1;
        var trackKey = GetTrackExportKey(track);
        var topTrackName = $"{trackKey}__shape_top";
        var bottomTrackName = $"{trackKey}__shape_bottom";

        var addTopResult = await _exportService.AddShapeBarAsync(
            draftId,
            start,
            duration,
            frameWidth,
            frameHeight,
            barHeight,
            topTrackName,
            transformX: 0,
            transformY: topTransformY,
            relativeIndex: overlayRelativeIndex,
            ct: ct);
        EnsureApiSuccess(addTopResult, $"add_shape_bar failed for top shape bar track '{topTrackName}'");

        var addBottomResult = await _exportService.AddShapeBarAsync(
            draftId,
            start,
            duration,
            frameWidth,
            frameHeight,
            barHeight,
            bottomTrackName,
            transformX: 0,
            transformY: bottomTransformY,
            relativeIndex: overlayRelativeIndex,
            ct: ct);
        EnsureApiSuccess(addBottomResult, $"add_shape_bar failed for bottom shape bar track '{bottomTrackName}'");
    }

    private static double ConvertEditorFontSizeToCapCut(double? editorFontSize)
    {
        // Editor TextOverlayElement font size uses a larger visual scale (e.g. 32 default).
        // CapCut's text style size scale is smaller and non-linear in perception:
        // - around 44 in editor maps well to ~6.5 in CapCut
        // - around 71 in editor maps better to ~10 (not ~10.4)
        // Keep lower sizes unchanged and slightly damp large sizes.
        const double fallbackCapCutSize = 6.5;
        const double baseRatio = 0.147;
        const double dampedRatio = 0.141;
        const double dampingStartSize = 48.0;
        const double dampingFullSize = 72.0;

        if (editorFontSize is not > 0)
            return fallbackCapCutSize;

        var size = editorFontSize.Value;
        var ratio = baseRatio;

        if (size > dampingStartSize)
        {
            var t = Math.Clamp((size - dampingStartSize) / (dampingFullSize - dampingStartSize), 0.0, 1.0);
            ratio = baseRatio + (dampedRatio - baseRatio) * t;
        }

        return Math.Clamp(size * ratio, 2.0, 72.0);
    }

    private static double? TryGetTextElementFontSize(Element? textElement)
    {
        if (textElement is null || string.IsNullOrWhiteSpace(textElement.PropertiesJson))
            return null;

        try
        {
            using var props = JsonDocument.Parse(textElement.PropertiesJson);
            if (props.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (props.RootElement.TryGetProperty("FontSize", out var fontSizeEl)
                && fontSizeEl.ValueKind == JsonValueKind.Number
                && fontSizeEl.TryGetDouble(out var value)
                && value > 0)
            {
                return value;
            }
        }
        catch
        {
            // Ignore malformed element JSON and fallback to track style.
        }

        return null;
    }

    private sealed record TextElementStyleFlags(bool? IsBold, bool? IsItalic, bool? IsUnderline);

    private static TextElementStyleFlags? TryGetTextElementStyleFlags(Element? textElement)
    {
        if (textElement is null || string.IsNullOrWhiteSpace(textElement.PropertiesJson))
            return null;

        try
        {
            using var props = JsonDocument.Parse(textElement.PropertiesJson);
            if (props.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var hasAny = false;
            bool? isBold = null;
            bool? isItalic = null;
            bool? isUnderline = null;

            if (props.RootElement.TryGetProperty("IsBold", out var boldEl)
                && boldEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isBold = boldEl.GetBoolean();
                hasAny = true;
            }

            if (props.RootElement.TryGetProperty("IsItalic", out var italicEl)
                && italicEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isItalic = italicEl.GetBoolean();
                hasAny = true;
            }

            if (props.RootElement.TryGetProperty("IsUnderline", out var underlineEl)
                && underlineEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isUnderline = underlineEl.GetBoolean();
                hasAny = true;
            }

            return hasAny ? new TextElementStyleFlags(isBold, isItalic, isUnderline) : null;
        }
        catch
        {
            // Ignore malformed element JSON and fallback to track style.
            return null;
        }
    }

    private static string PrepareCapCutTextWithWordWrap(
        string text,
        double fixedWidthRatio,
        double capCutFontSize,
        int exportCanvasWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (fixedWidthRatio <= 0 || capCutFontSize <= 0 || exportCanvasWidth <= 0)
            return text;

        // Reserve side padding so line edges do not appear visually stuck to the box.
        var usableWidthPx = fixedWidthRatio * exportCanvasWidth * 0.93;
        if (usableWidthPx <= 8)
            return text;

        // Empirical average glyph width for mixed Vietnamese/Latin text in CapCut.
        var avgGlyphWidthPx = Math.Max(1.0, capCutFontSize * 0.56);
        var maxCharsPerLine = (int)Math.Floor(usableWidthPx / avgGlyphWidthPx);
        maxCharsPerLine = Math.Clamp(maxCharsPerLine, 8, 120);

        var normalized = text.Replace("\r\n", "\n");
        var paragraphs = normalized.Split('\n');
        var wrappedParagraphs = new List<string>(paragraphs.Length);

        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                wrappedParagraphs.Add(string.Empty);
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                wrappedParagraphs.Add(paragraph);
                continue;
            }

            var lines = new List<string>();
            var currentLine = string.Empty;

            foreach (var word in words)
            {
                // Fallback for extremely long tokens (URL / non-spaced strings).
                if (word.Length > maxCharsPerLine)
                {
                    if (!string.IsNullOrEmpty(currentLine))
                    {
                        lines.Add(currentLine);
                        currentLine = string.Empty;
                    }

                    for (var i = 0; i < word.Length; i += maxCharsPerLine)
                    {
                        var chunkLen = Math.Min(maxCharsPerLine, word.Length - i);
                        lines.Add(word.Substring(i, chunkLen));
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(currentLine))
                {
                    currentLine = word;
                    continue;
                }

                var candidate = $"{currentLine} {word}";
                if (candidate.Length <= maxCharsPerLine)
                {
                    currentLine = candidate;
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = word;
                }
            }

            if (!string.IsNullOrEmpty(currentLine))
                lines.Add(currentLine);

            wrappedParagraphs.Add(string.Join('\n', lines));
        }

        return string.Join('\n', wrappedParagraphs);
    }

    private static (double fixedWidthRatio, double fixedHeightRatio) ComputeCapCutTextBounds(
        Element? element,
        int canvasWidth,
        int canvasHeight)
    {
        if (element is null || canvasWidth <= 0 || canvasHeight <= 0)
            return (0, 0);

        // Calibrate editor text box width to CapCut's wrapping behavior.
        // CapCut tends to render more characters per line for the same visual box width,
        // so we intentionally shrink exported fixed_width to preserve line breaks.
        // Additional 4% total shrink (~2% each side) per visual calibration.
        const double widthCalibrationRatio = 0.672;
        const double maxLineWidthRatio = 0.82; // Matches CapCut line_max_width default.
        // Editor TextOverlay template uses BorderThickness=2 and TextBlock Padding=8,4.
        // So visible text layout width is ~20px smaller than element.Width.
        const double editorHorizontalInsetPx = 20.0;

        var fixedWidthRatio = 0d;
        if (element.Width > 0)
        {
            var effectiveEditorTextWidthPx = Math.Max(1.0, element.Width - editorHorizontalInsetPx);
            var editorWidthRatio = effectiveEditorTextWidthPx / canvasWidth;
            fixedWidthRatio = Math.Clamp(editorWidthRatio * widthCalibrationRatio, 0.05, maxLineWidthRatio);
        }

        var isFixedSizingMode = false;
        if (!string.IsNullOrWhiteSpace(element.PropertiesJson))
        {
            try
            {
                using var props = JsonDocument.Parse(element.PropertiesJson);
                if (props.RootElement.ValueKind == JsonValueKind.Object &&
                    props.RootElement.TryGetProperty("sizingMode", out var sizingModeEl))
                {
                    var sizingMode = sizingModeEl.GetString();
                    isFixedSizingMode = string.Equals(sizingMode, "Fixed", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Ignore malformed element properties and fall back to auto-height behavior.
            }
        }

        // Only enforce fixed height when the editor explicitly uses fixed sizing mode.
        var fixedHeightRatio = isFixedSizingMode && element.Height > 0
            ? Math.Clamp(element.Height / canvasHeight, 0.02, 1.0)
            : 0;

        return (fixedWidthRatio, fixedHeightRatio);
    }

    private static string GetTrackExportKey(Track track)
    {
        // Always include Order to guarantee uniqueness across tracks of the same type.
        // Previously, two visual tracks with the same Name (or both unnamed with same Order)
        // would produce the same key, causing all segments to be merged into one CapCut track.
        if (!string.IsNullOrWhiteSpace(track.Name))
            return $"{track.Name}_{track.Order}";

        return $"{track.TrackType}_{track.Order}";
    }

    private static void EnsureApiSuccess(JsonDocument? result, string context)
    {
        if (result is null)
            throw new InvalidOperationException($"{context}: API returned null response.");

        if (result.RootElement.TryGetProperty("success", out var successEl)
            && successEl.ValueKind == JsonValueKind.False)
        {
            var apiError = result.RootElement.TryGetProperty("error", out var errorEl)
                ? errorEl.GetString()
                : "Unknown error";
            throw new InvalidOperationException($"{context}: {apiError}");
        }
    }

    partial void OnIsExportingChanged(bool value)
    {
        ExportToCapCutCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
