#nullable enable
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Builds render segments (visual, text, audio, visualizer) from project data
/// and canvas element snapshots. Extracted from RenderViewModel to keep the
/// ViewModel focused on UI orchestration only.
/// </summary>
public static class RenderSegmentBuilder
{
    // ── Unified ZOrder ──────────────────────────────────────────────
    // Tracks participate in a type-tiered z-order system so that overlay tracks
    // (text, effect) always render on top of background tracks (visual), matching
    // commercial editor behavior. Within each tier, Track.Order determines
    // stacking (lower Order = foreground within that tier).
    //
    // Tiers:
    //   0 – Visual   (background images/video)
    //   1 – Text     (text overlays — always above visuals)
    //   2 – Effect   (graphical effects — above text)
    //
    // Each track is allocated a slot of ZOrderSlotSize values within its tier.
    public const int ZOrderSlotSize = 100;
    private const int ZOrderTierSize = 10_000;

    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm"];

    /// <summary>
    /// Resolve the primary audio file path for the project.
    /// First tries <see cref="Project.AudioPath"/> (legacy), then falls back to the
    /// first audio segment asset on any visible audio track.
    /// Returns <c>null</c> when no audio file can be found.
    /// </summary>
    public static string? ResolveProjectAudioPath(Project project)
    {
        Log.Information("ResolveProjectAudioPath: legacy AudioPath='{AudioPath}', Tracks={TrackCount}, Assets={AssetCount}",
            project.AudioPath ?? "(null)",
            project.Tracks?.Count ?? -1,
            project.Assets?.Count ?? -1);

        // Try legacy AudioPath first
        if (!string.IsNullOrWhiteSpace(project.AudioPath) && File.Exists(project.AudioPath))
        {
            Log.Information("ResolveProjectAudioPath: using legacy AudioPath: {Path}", project.AudioPath);
            return project.AudioPath;
        }

        // Fallback: resolve from the first audio segment with a valid asset file
        var audioTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, "audio", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .ToList();
        Log.Information("ResolveProjectAudioPath: found {Count} visible audio tracks", audioTracks?.Count ?? 0);

        var fallbackAsset = audioTracks?
            .SelectMany(t => t.Segments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.BackgroundAssetId))
            .Select(s => project.Assets?.FirstOrDefault(a => a.Id == s.BackgroundAssetId))
            .FirstOrDefault(a => a != null && !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath));

        if (fallbackAsset != null)
        {
            Log.Information("ResolveProjectAudioPath: resolved from audio segment asset: {Path}", fallbackAsset.FilePath);
            return fallbackAsset.FilePath;
        }

        Log.Warning("ResolveProjectAudioPath: no audio file found");
        return null;
    }

    /// <summary>
    /// Build a map from Track.Id → ZOrder base value.
    /// Uses type-based tiers (Visual &lt; Text &lt; Effect) so that text overlays
    /// always render on top of visual backgrounds, regardless of Track.Order.
    /// Within each tier, tracks are sorted by Order descending (higher Order =
    /// background within that tier, lower Order = foreground).
    /// </summary>
    public static Dictionary<string, int> ComputeTrackZOrderMap(Project project)
    {
        var map = new Dictionary<string, int>();
        if (project.Tracks == null) return map;

        var visible = project.Tracks.Where(t => t.IsVisible).ToList();

        // Assign a tier per track type so overlay types always sit above backgrounds
        static int GetTier(string? trackType) => trackType?.ToLowerInvariant() switch
        {
            "visual" => 0,
            "audio"  => 0,  // audio tracks have no visuals but share the base tier
            "text"   => 1,
            "effect" => 2,
            _        => 0
        };

        // Within each tier, lower Order = foreground (higher ZOrder)
        var grouped = visible
            .GroupBy(t => GetTier(t.TrackType))
            .OrderBy(g => g.Key);

        foreach (var tier in grouped)
        {
            var tierBase = tier.Key * ZOrderTierSize;
            var sorted = tier.OrderByDescending(t => t.Order).ToList();
            for (int i = 0; i < sorted.Count; i++)
                map[sorted[i].Id] = tierBase + i * ZOrderSlotSize;
        }

        return map;
    }

    /// <summary>
    /// Collect visual (image/video) segments from ALL visible visual tracks, ordered back→front.
    /// Tracks are iterated from highest Order (background) → lowest Order (foreground) so that
    /// segments on background tracks receive lower ZOrder values (overlaid first in FFmpeg)
    /// and foreground tracks receive higher ZOrder values (overlaid last = on top).
    /// When a linked canvas element exists for a segment, its position and size are mapped to
    /// render coordinates so the output matches the canvas preview exactly.
    /// </summary>
    public static List<RenderVisualSegment> BuildTimelineVisualSegments(
        Project project, int renderWidth, int renderHeight,
        IReadOnlyList<CanvasElement>? elements, double canvasWidth, double canvasHeight,
        Dictionary<string, int>? zOrderMap = null)
    {
        var visualTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderByDescending(t => t.Order)
            .ToList();

        if (visualTracks == null || visualTracks.Count == 0)
            return [];

        var segments = new List<RenderVisualSegment>();
        foreach (var track in visualTracks)
        {
            if (track.Segments == null) continue;
            var trackBase = zOrderMap != null && zOrderMap.TryGetValue(track.Id, out var zb) ? zb : 0;
            var localIdx = 0;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId) || segment.EndTime <= segment.StartTime)
                    continue;

                var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
                {
                    Log.Warning("Render: skipping segment {SegId} — asset not found or file missing (AssetId={AssetId}, Path={Path})",
                        segment.Id, segment.BackgroundAssetId, asset?.FilePath ?? "(null)");
                    continue;
                }

                var renderSeg = new RenderVisualSegment
                {
                    SourcePath          = asset.FilePath,
                    StartTime           = segment.StartTime,
                    EndTime             = segment.EndTime,
                    IsVideo             = IsVideoAsset(asset),
                    SourceOffsetSeconds = 0,
                    ZOrder              = trackBase + localIdx++
                };

                // Sync position and size from the linked canvas element when available.
                var linkedElement = elements?.FirstOrDefault(e =>
                    string.Equals(e.SegmentId, segment.Id, StringComparison.Ordinal));

                // Skip image segment creation for VisualizerElement positions —
                // visualizers are rendered separately by BuildVisualizerSegmentsAsync.
                if (linkedElement is VisualizerElement)
                    continue;

                if (linkedElement != null && canvasWidth > 0 && canvasHeight > 0 && renderWidth > 0 && renderHeight > 0)
                {
                    var scaleX = renderWidth  / canvasWidth;
                    var scaleY = renderHeight / canvasHeight;

                    var overlayX = (int)Math.Round(linkedElement.X * scaleX);
                    var overlayY = (int)Math.Round(linkedElement.Y * scaleY);
                    var scaleW   = (int)Math.Round(linkedElement.Width  * scaleX);
                    var scaleH   = (int)Math.Round(linkedElement.Height * scaleY);

                    // Clamp to valid render bounds
                    overlayX = Math.Max(0, Math.Min(overlayX, renderWidth  - 1));
                    overlayY = Math.Max(0, Math.Min(overlayY, renderHeight - 1));
                    scaleW   = Math.Max(1, Math.Min(scaleW,   renderWidth));
                    scaleH   = Math.Max(1, Math.Min(scaleH,   renderHeight));

                    renderSeg.OverlayX     = overlayX.ToString(CultureInfo.InvariantCulture);
                    renderSeg.OverlayY     = overlayY.ToString(CultureInfo.InvariantCulture);
                    renderSeg.ScaleWidth   = scaleW;
                    renderSeg.ScaleHeight  = scaleH;
                }
                else if (renderWidth > 0 && renderHeight > 0)
                {
                    // No linked canvas element: apply the track's ImageLayoutPreset so
                    // Square_Center / Widescreen_Center are respected in the render output.
                    var preset = string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
                        ? Models.ImageLayoutPresets.FullFrame
                        : track.ImageLayoutPreset;
                    var (rx, ry, rw, rh) = RenderHelper.ComputeImageRect(preset, renderWidth, renderHeight);
                    renderSeg.OverlayX    = ((int)Math.Round(rx)).ToString(CultureInfo.InvariantCulture);
                    renderSeg.OverlayY    = ((int)Math.Round(ry)).ToString(CultureInfo.InvariantCulture);
                    renderSeg.ScaleWidth  = (int)Math.Round(rw);
                    renderSeg.ScaleHeight = (int)Math.Round(rh);
                }

                // Resolve motion preset: segment-level override > track auto-random > None.
                // Only applies to still images; video segments keep MotionPreset = None.
                if (!renderSeg.IsVideo)
                {
                    var segPreset = segment.MotionPreset;
                    if (string.IsNullOrWhiteSpace(segPreset) || segPreset == Models.MotionPresets.None)
                    {
                        // Segment has no explicit preset — check track auto-motion
                        if (track.AutoMotionEnabled)
                            segPreset = Models.MotionPresets.GetRandomPreset(segment.Id);
                        else
                            segPreset = Models.MotionPresets.None;
                    }

                    renderSeg.MotionPreset = segPreset;

                    // Resolve intensity: segment-level > track-level fallback
                    renderSeg.MotionIntensity = segment.MotionIntensity ?? track.MotionIntensity;
                }

                segments.Add(renderSeg);
            }
        }

        return segments;
    }

    /// <summary>
    /// Rasterize text elements to PNG images (WYSIWYG) and return them as visual overlay segments.
    /// Tracks are iterated from highest Order (background) → lowest Order (foreground) so that
    /// text on background tracks is overlaid first and foreground text appears on top.
    /// This ensures text wrapping, alignment, and styling in the export match the canvas preview exactly.
    /// </summary>
    public static List<RenderVisualSegment> BuildRasterizedTextSegments(
        Project project, int renderWidth, int renderHeight,
        IReadOnlyList<CanvasElement>? elements, double canvasWidth, double canvasHeight,
        Dictionary<string, int>? zOrderMap = null)
    {
        var textTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, "text", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderByDescending(t => t.Order)
            .ToList();

        if (textTracks == null || textTracks.Count == 0)
        {
            Log.Information("BuildRasterizedTextSegments: no visible text tracks found (total tracks={Count})",
                project.Tracks?.Count ?? 0);
            return [];
        }

        var totalTextSegs = textTracks.Sum(t => t.Segments?.Count ?? 0);
        Log.Information("BuildRasterizedTextSegments: {TrackCount} text tracks, {SegCount} total segments, {ElCount} snapshot elements",
            textTracks.Count, totalTextSegs, elements?.Count ?? 0);

        // Create temp directory for rasterized text images
        // Use a unique temp directory per render to avoid file-lock conflicts
        // when a previous FFmpeg process still holds earlier PNGs open.
        var textImageDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "render_text_img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(textImageDir);

        // First pass: collect all work items with pre-assigned indices (needed for
        // deterministic file names and z-order, which must match across runs).
        var workItems = new List<(int index, Segment segment, CanvasElement? linkedElement,
                                  int trackBase, int localIdx, TextRasterizeOptions options,
                                  int overlayX, int overlayY, int imgWidth, int imgHeight)>();
        var index = 0;
        foreach (var track in textTracks)
        {
            if (track.Segments == null) continue;
            var trackBase = zOrderMap != null && zOrderMap.TryGetValue(track.Id, out var zb) ? zb : 0;
            var localIdx = 0;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.Text) || segment.EndTime <= segment.StartTime)
                {
                    Log.Debug("BuildRasterizedTextSegments: skipping segment {Id} — Text='{Text}', Start={Start}, End={End}",
                        segment.Id, segment.Text ?? "(null)", segment.StartTime, segment.EndTime);
                    continue;
                }

                // Look up linked canvas element to get position/style/size data
                var linkedElement = elements?.FirstOrDefault(e => e.SegmentId == segment.Id);
                Log.Debug("BuildRasterizedTextSegments: segment {Id} Text='{Text}' linkedElement={HasEl} ZOrder={Z}",
                    segment.Id, segment.Text?.Length > 30 ? segment.Text[..30] + "…" : segment.Text,
                    linkedElement != null, trackBase + localIdx);

                // Build rasterization options from element properties
                var options = new TextRasterizeOptions
                {
                    Text = segment.Text,
                    CanvasWidth = canvasWidth,
                    CanvasHeight = canvasHeight
                };

                int overlayX = 0, overlayY = 0;
                int imgWidth = renderWidth, imgHeight = 80;

                if (linkedElement != null && canvasWidth > 0 && canvasHeight > 0)
                {
                    var scaleX = (double)renderWidth / canvasWidth;
                    var scaleY = (double)renderHeight / canvasHeight;

                    overlayX = (int)Math.Round(linkedElement.X * scaleX);
                    overlayY = (int)Math.Round(linkedElement.Y * scaleY);
                    imgWidth = Math.Max(1, (int)Math.Round(linkedElement.Width * scaleX));
                    imgHeight = Math.Max(1, (int)Math.Round(linkedElement.Height * scaleY));

                    // Clamp position
                    overlayX = Math.Max(0, Math.Min(overlayX, renderWidth - 1));
                    overlayY = Math.Max(0, Math.Min(overlayY, renderHeight - 1));

                    if (linkedElement is TextOverlayElement overlay)
                    {
                        options.FontSize = (float)CoordinateMapper.ScaleFontSize(overlay.FontSize, canvasHeight, renderHeight);
                        options.ColorHex = overlay.ColorHex;
                        options.FontFamily = overlay.FontFamily;
                        options.IsBold = overlay.IsBold;
                        options.IsItalic = overlay.IsItalic;
                        options.Alignment = overlay.Alignment switch
                        {
                            Core.Models.TextAlignment.Left => TextRasterizeAlignment.Left,
                            Core.Models.TextAlignment.Right => TextRasterizeAlignment.Right,
                            _ => TextRasterizeAlignment.Center
                        };
                        options.LineHeightMultiplier = (float)overlay.LineHeight;
                        options.LetterSpacing = (float)overlay.LetterSpacing;
                        options.HasShadow = overlay.HasShadow;
                        options.ShadowColorHex = overlay.ShadowColorHex;
                        options.ShadowOffsetX = overlay.ShadowOffsetX;
                        options.ShadowOffsetY = overlay.ShadowOffsetY;
                        options.ShadowBlur = overlay.ShadowBlur;
                        options.HasOutline = overlay.HasOutline;
                        options.OutlineColorHex = overlay.OutlineColorHex;
                        options.OutlineThickness = overlay.OutlineThickness;
                        if (overlay.HasBackground)
                        {
                            options.DrawBox = true;
                            options.BoxColor = overlay.BackgroundColorHex;
                            options.BoxAlpha = (float)overlay.BackgroundOpacity;
                            options.BackgroundCornerRadius = (float)overlay.BackgroundCornerRadius;
                        }
                        else
                        {
                            options.DrawBox = false;
                        }
                    }
                }
                else
                {
                    // No linked element — use defaults
                    options.FontSize = CoordinateMapper.ScaleFontSize(24, canvasHeight > 0 ? canvasHeight : renderHeight, renderHeight);
                    imgHeight = (int)(renderHeight * 0.1);
                    overlayY = (int)(renderHeight * 0.8);
                    overlayX = 0;
                    imgWidth = renderWidth;
                }

                options.Width = imgWidth;
                options.Height = imgHeight;

                workItems.Add((index, segment, linkedElement, trackBase, localIdx, options,
                               overlayX, overlayY, imgWidth, imgHeight));
                localIdx++;
                index++;
            }
        }

        // Second pass: rasterize all text segments in parallel (each is CPU-bound and independent)
        var segments = new RenderVisualSegment?[workItems.Count];
        int rasterFailures = 0;
        Parallel.For(0, workItems.Count, i =>
        {
            var item = workItems[i];
            var imagePath = Path.Combine(textImageDir, $"text_{item.index}.png");
            try
            {
                TextRasterizer.RenderToFile(item.options, imagePath);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref rasterFailures);
                Log.Warning(ex, "Failed to rasterize text segment {Index}, skipping", item.index);
                return;
            }

            segments[i] = new RenderVisualSegment
            {
                SourcePath  = imagePath,
                StartTime   = item.segment.StartTime,
                EndTime     = item.segment.EndTime,
                IsVideo     = false,
                OverlayX    = item.overlayX.ToString(CultureInfo.InvariantCulture),
                OverlayY    = item.overlayY.ToString(CultureInfo.InvariantCulture),
                ScaleWidth  = item.imgWidth,
                ScaleHeight = item.imgHeight,
                ZOrder      = item.trackBase + item.localIdx
            };
        });

        var result = segments.Where(s => s != null).Select(s => s!).ToList();
        if (rasterFailures > 0)
            Log.Error("Text rasterization: {Failed}/{Total} segments failed — rendered text may be incomplete",
                rasterFailures, workItems.Count);
        else
            Log.Information("Text rasterization: {Count} segments rasterized successfully", result.Count);

        return result;
    }

    /// <summary>
    /// Bake every visible <see cref="VisualizerElement"/> on the canvas into a
    /// transparent video file and return them as <see cref="RenderVisualSegment"/>s.
    /// Each element is rendered offline using the same SkiaSharp renderers that the
    /// live preview uses, so the output is a faithful 1:1 match.
    /// </summary>
    public static async Task<List<RenderVisualSegment>> BuildVisualizerSegmentsAsync(
        Project project,
        int renderWidth,
        int renderHeight,
        IReadOnlyList<CanvasElement>? elements,
        double canvasWidth,
        double canvasHeight,
        int frameRate,
        CancellationToken ct,
        Dictionary<string, int>? zOrderMap = null)
    {
        if (elements == null || canvasWidth <= 0 || canvasHeight <= 0)
        {
            Log.Warning("BuildVisualizerSegments: skipped — elements={Null}, canvas={W}x{H}",
                elements == null ? "null" : elements.Count.ToString(), canvasWidth, canvasHeight);
            return [];
        }

        var ffmpegPath = FFmpegService.GetFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            Log.Error("BuildVisualizerSegments: FFmpeg path is null — was InitializeAsync called before this method?");
            return [];
        }

        // Determine output duration from existing segments (or audio file length)
        double maxEndTime = ResolveRenderDuration(project);
        if (maxEndTime <= 0)
        {
            Log.Warning("BuildVisualizerSegments: skipped — resolved render duration is {Duration:F2}s", maxEndTime);
            return [];
        }

        var audioFilePath = ResolveProjectAudioPath(project) ?? string.Empty;
        Log.Information("BuildVisualizerSegments: resolved audioFilePath='{AudioPath}', exists={Exists}",
            audioFilePath, !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath));

        var segments = new List<RenderVisualSegment>();
        var invariant = CultureInfo.InvariantCulture;
        var scaleX = renderWidth  / canvasWidth;
        var scaleY = renderHeight / canvasHeight;

        // Prepare bake tasks for all visualizer elements so they run in parallel.
        // Each task is independent (separate audio reader, separate FFmpeg process).
        var bakeTasks = new List<Task<(VisualizerElement element, string? bakedPath, double vizStart, double vizEnd, string? vizTrackId)>>();

        foreach (var element in elements.OfType<VisualizerElement>())
        {
            // If the visualizer is bound to a specific segment, limit its time range
            double vizStart = 0;
            double vizEnd   = maxEndTime;
            string? vizTrackId = null;

            if (!string.IsNullOrEmpty(element.SegmentId))
            {
                var linked = project.Tracks?
                    .SelectMany(t => (t.Segments ?? []).Select(s => (track: t, segment: s)))
                    .FirstOrDefault(p => p.segment.Id == element.SegmentId);
                if (linked?.segment != null)
                {
                    vizStart = linked.Value.segment.StartTime;
                    vizEnd   = linked.Value.segment.EndTime;
                    vizTrackId = linked.Value.track.Id;
                }
            }

            if (vizEnd <= vizStart) continue;

            Log.Information(
                "Baking VisualizerElement {Id} ({Style}) [{Start:F2}–{End:F2}s]",
                element.Id, element.Style, vizStart, vizEnd);

            // Capture loop variables for the async lambda
            var capturedStart = vizStart;
            var capturedEnd = vizEnd;
            var capturedTrackId = vizTrackId;
            var capturedElement = element;

            bakeTasks.Add(Task.Run(async () =>
            {
                var path = await OfflineVisualizerBaker.BakeAsync(
                    capturedElement,
                    audioFilePath,
                    renderWidth, renderHeight,
                    (int)canvasWidth, (int)canvasHeight,
                    capturedStart, capturedEnd,
                    frameRate,
                    ffmpegPath,
                    ct);
                return (capturedElement, path, capturedStart, capturedEnd, capturedTrackId);
            }, ct));
        }

        // Await all bake tasks in parallel
        var bakeResults = await Task.WhenAll(bakeTasks);

        foreach (var (element, bakedPath, vizStart, vizEnd, vizTrackId) in bakeResults)
        {
            if (string.IsNullOrEmpty(bakedPath)) continue;

            int overlayX = Math.Max(0, (int)Math.Round(element.X * scaleX));
            int overlayY = Math.Max(0, (int)Math.Round(element.Y * scaleY));
            int overlayW = Math.Max(1, (int)Math.Round(element.Width  * scaleX));
            int overlayH = Math.Max(1, (int)Math.Round(element.Height * scaleY));

            // Compute ZOrder from the linked track's slot in the global z-order map.
            // If no track found (unlinked visualizer), place it at the very top.
            int vizZOrder;
            if (vizTrackId != null && zOrderMap != null && zOrderMap.TryGetValue(vizTrackId, out var trackZBase))
                vizZOrder = trackZBase + segments.Count;
            else
                vizZOrder = (zOrderMap?.Values.DefaultIfEmpty(0).Max() ?? 0) + ZOrderSlotSize + segments.Count;

            segments.Add(new RenderVisualSegment
            {
                SourcePath          = bakedPath,
                StartTime           = vizStart,
                EndTime             = vizEnd,
                IsVideo             = true,
                HasAlpha            = true,
                SourceOffsetSeconds = 0,
                OverlayX            = overlayX.ToString(invariant),
                OverlayY            = overlayY.ToString(invariant),
                ScaleWidth          = overlayW,
                ScaleHeight         = overlayH,
                ZOrder              = vizZOrder
            });
        }

        return segments;
    }

    /// <summary>
    /// Derive the total render duration from the project's timeline tracks.
    /// Uses the furthest segment end time only — the audio file length is intentionally
    /// ignored so the rendered video matches the timeline, not the raw audio file.
    /// </summary>
    public static double ResolveRenderDuration(Project project)
    {
        // Max end time across all track segments
        var trackMax = project.Tracks?
            .SelectMany(t => t.Segments ?? [])
            .Select(s => s.EndTime)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        return trackMax;
    }

    /// <summary>
    /// Collect extra audio clip segments from all visible audio tracks.
    /// </summary>
    public static List<RenderAudioSegment> BuildTimelineAudioSegments(Project project)
    {
        var audioTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, "audio", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderBy(t => t.Order)
            .ToList();

        if (audioTracks == null || audioTracks.Count == 0)
            return [];

        var segments = new List<RenderAudioSegment>();
        foreach (var track in audioTracks)
        {
            if (track.Segments == null) continue;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (segment.EndTime <= segment.StartTime || string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                    continue;

                var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
                    continue;

                segments.Add(new RenderAudioSegment
                {
                    SourcePath          = asset.FilePath,
                    StartTime           = segment.StartTime,
                    EndTime             = segment.EndTime,
                    Volume              = segment.Volume,
                    FadeInDuration      = segment.FadeInDuration,
                    FadeOutDuration     = segment.FadeOutDuration,
                    SourceOffsetSeconds = segment.SourceStartOffset
                });
            }
        }

        return segments;
    }

    /// <summary>
    /// Check if an asset is a video file based on its type or extension.
    /// </summary>
    public static bool IsVideoAsset(Asset asset)
    {
        if (string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(asset.FilePath);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
