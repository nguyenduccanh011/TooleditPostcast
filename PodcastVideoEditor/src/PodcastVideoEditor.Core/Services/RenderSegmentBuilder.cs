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
    // All tracks participate in a single global z-order based on Track.Order.
    // Each track is allocated a slot of ZOrderSlotSize values.
    // Tracks are iterated from highest Order (background) → lowest Order (foreground),
    // so background tracks get low ZOrder (overlaid first in FFmpeg) and
    // foreground tracks get high ZOrder (overlaid last = on top).
    //
    // This replaces the old fixed ranges (Visual=0-999, Text=1000-1999, Visualizer=2000+)
    // and allows any track type at any z-order position.
    public const int ZOrderSlotSize = 100;

    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm"];

    /// <summary>
    /// Build a map from Track.Id → ZOrder base value.
    /// Tracks are sorted by Order descending (background first = low ZOrder),
    /// each gets a slot of <see cref="ZOrderSlotSize"/> values.
    /// This ensures that Track Order 0 (top/foreground) gets the highest ZOrder range.
    /// </summary>
    public static Dictionary<string, int> ComputeTrackZOrderMap(Project project)
    {
        var map = new Dictionary<string, int>();
        if (project.Tracks == null) return map;

        var sorted = project.Tracks
            .Where(t => t.IsVisible)
            .OrderByDescending(t => t.Order)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
            map[sorted[i].Id] = i * ZOrderSlotSize;

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
            return [];

        // Create temp directory for rasterized text images
        var textImageDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "render_text_img");
        Directory.CreateDirectory(textImageDir);

        var segments = new List<RenderVisualSegment>();
        var index = 0;
        foreach (var track in textTracks)
        {
            if (track.Segments == null) continue;
            var trackBase = zOrderMap != null && zOrderMap.TryGetValue(track.Id, out var zb) ? zb : 0;
            var localIdx = 0;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.Text) || segment.EndTime <= segment.StartTime)
                    continue;

                // Look up linked canvas element to get position/style/size data
                var linkedElement = elements?.FirstOrDefault(e => e.SegmentId == segment.Id);

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

                    if (linkedElement is TitleElement title)
                    {
                        options.FontSize = (float)CoordinateMapper.ScaleFontSize(title.FontSize, canvasHeight, renderHeight);
                        options.ColorHex = title.ColorHex;
                        options.FontFamily = title.FontFamily;
                        options.IsBold = title.IsBold;
                        options.IsItalic = title.IsItalic;
                        options.Alignment = title.Alignment switch
                        {
                            Core.Models.TextAlignment.Left => TextRasterizeAlignment.Left,
                            Core.Models.TextAlignment.Right => TextRasterizeAlignment.Right,
                            _ => TextRasterizeAlignment.Center
                        };
                    }
                    else if (linkedElement is TextElement text)
                    {
                        options.FontSize = (float)CoordinateMapper.ScaleFontSize(text.FontSize, canvasHeight, renderHeight);
                        options.ColorHex = text.ColorHex;
                        options.Alignment = TextRasterizeAlignment.Center;
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

                // Rasterize text to PNG
                var imagePath = Path.Combine(textImageDir, $"text_{index}.png");
                try
                {
                    TextRasterizer.RenderToFile(options, imagePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to rasterize text segment {Index}, skipping", index);
                    index++;
                    continue;
                }

                segments.Add(new RenderVisualSegment
                {
                    SourcePath  = imagePath,
                    StartTime   = segment.StartTime,
                    EndTime     = segment.EndTime,
                    IsVideo     = false,
                    OverlayX    = overlayX.ToString(CultureInfo.InvariantCulture),
                    OverlayY    = overlayY.ToString(CultureInfo.InvariantCulture),
                    ScaleWidth  = imgWidth,
                    ScaleHeight = imgHeight,
                    ZOrder      = trackBase + localIdx++
                });

                index++;
            }
        }

        return segments;
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

        var audioFilePath = project.AudioPath ?? string.Empty;

        var segments = new List<RenderVisualSegment>();
        var invariant = CultureInfo.InvariantCulture;
        var scaleX = renderWidth  / canvasWidth;
        var scaleY = renderHeight / canvasHeight;

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

            var bakedPath = await OfflineVisualizerBaker.BakeAsync(
                element,
                audioFilePath,
                renderWidth, renderHeight,
                (int)canvasWidth, (int)canvasHeight,
                vizStart, vizEnd,
                frameRate,
                ffmpegPath,
                ct);

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
    /// Derive the total render duration from the project's tracks and audio file.
    /// Used to set the end time when baking full-timeline visualizer elements.
    /// </summary>
    public static double ResolveRenderDuration(Project project)
    {
        // Max end time across all track segments
        var trackMax = project.Tracks?
            .SelectMany(t => t.Segments ?? [])
            .Select(s => s.EndTime)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        // Audio file duration (if available)
        double audioMax = 0;
        if (!string.IsNullOrWhiteSpace(project.AudioPath)
            && File.Exists(project.AudioPath))
        {
            try
            {
                using var reader = new NAudio.Wave.AudioFileReader(project.AudioPath);
                audioMax = reader.TotalTime.TotalSeconds;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ResolveRenderDuration: could not read audio duration");
            }
        }

        return Math.Max(trackMax, audioMax);
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
