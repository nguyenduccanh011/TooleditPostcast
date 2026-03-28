#nullable enable
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Builds a <see cref="CompositionPlan"/> from project data and canvas element snapshots.
/// Replaces the ad-hoc segment building that was previously scattered across RenderViewModel.
///
/// Commercial pattern: equivalent to DaVinci Resolve's "timeline flattener" or
/// Premiere's "render tree builder" — produces an ordered, validated intermediate
/// representation before passing to the codec/filter backend.
/// </summary>
public interface ICompositionBuilder
{
    /// <summary>
    /// Build a complete composition plan from the given project, render settings, and element snapshot.
    /// </summary>
    Task<CompositionPlan> BuildPlanAsync(CompositionBuildContext context, CancellationToken ct = default);
}

/// <summary>
/// Input context for <see cref="ICompositionBuilder.BuildPlanAsync"/>.
/// Collects all data needed so the builder has no external dependencies.
/// </summary>
public class CompositionBuildContext
{
    public required Project Project { get; init; }
    public required IReadOnlyList<CanvasElement>? Elements { get; init; }
    public required double CanvasWidth { get; init; }
    public required double CanvasHeight { get; init; }
    public required int RenderWidth { get; init; }
    public required int RenderHeight { get; init; }
    public required int FrameRate { get; init; }
    public required string OutputPath { get; init; }
    public required string VideoCodec { get; init; }
    public required string AudioCodec { get; init; }
    public required string Quality { get; init; }
    public required string AspectRatio { get; init; }
    public required string ScaleMode { get; init; }
    public required double PrimaryAudioVolume { get; init; }
}

/// <summary>
/// Default composition builder implementation.
/// </summary>
public class CompositionBuilder : ICompositionBuilder
{
    /// <summary>Simple mutable int wrapper so async methods can share a counter.</summary>
    private sealed class ZOrderCounter
    {
        public int Value;
        public int Next() => Value++;
    }

    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm"];

    public async Task<CompositionPlan> BuildPlanAsync(CompositionBuildContext ctx, CancellationToken ct = default)
    {
        var layers = new List<CompositionLayer>();
        var zOrder = new ZOrderCounter();
        var registry = ElementSegmentRegistry.Build(ctx.Elements, ctx.Project.Tracks);

        // ── Unified single-pass compositing ──────────────────────────────
        // All visible tracks are processed in a single z-order space, sorted
        // from highest Order (background) → lowest Order (foreground).
        // This matches commercial editors (CapCut, Premiere, DaVinci) where
        // Track.Order is the single source of truth for visual stacking.

        var allVisibleTracks = ctx.Project.Tracks?
            .Where(t => t.IsVisible)
            .OrderByDescending(t => t.Order) // background first → low z → overlaid first
            .ToList() ?? [];

        var textImageDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PodcastVideoEditor", "render_text_img");
        System.IO.Directory.CreateDirectory(textImageDir);
        int textIndex = 0;

        // Pre-resolve audio path for visualizer baking (shared across all visualizers)
        var resolvedAudioPath = RenderSegmentBuilder.ResolveProjectAudioPath(ctx.Project) ?? string.Empty;

        // Build O(1) asset lookup to avoid repeated linear scans
        var assetMap = (ctx.Project.Assets ?? []).Where(a => a.Id != null)
            .ToDictionary(a => a.Id!, StringComparer.Ordinal);

        foreach (var track in allVisibleTracks)
        {
            if (track.Segments == null) continue;

            var trackType = track.TrackType?.ToLowerInvariant() ?? "";

            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (segment.EndTime <= segment.StartTime) continue;

                switch (trackType)
                {
                    case TrackTypes.Visual:
                        var visualLayer = BuildSingleVisualLayer(ctx, segment, track, zOrder, registry, assetMap);
                        if (visualLayer != null) layers.Add(visualLayer);
                        break;

                    case TrackTypes.Text:
                        var textLayer = BuildSingleTextLayer(ctx, segment, zOrder, registry, textImageDir, ref textIndex);
                        if (textLayer != null) layers.Add(textLayer);
                        break;

                    case TrackTypes.Effect:
                        // Effect tracks hold VisualizerElements — process their linked elements
                        var linkedElement = registry.GetElementForSegment(segment.Id);
                        if (linkedElement is VisualizerElement vizElement)
                        {
                            var vizLayer = await BuildSingleVisualizerLayerAsync(ctx, vizElement, segment, zOrder, resolvedAudioPath, ct);
                            if (vizLayer != null) layers.Add(vizLayer);
                        }
                        break;

                    // TrackTypes.Audio tracks are handled separately in BuildAudioLayers
                }
            }
        }

        // Also catch VisualizerElements that are NOT on effect tracks (orphaned / global)
        if (ctx.Elements != null)
        {
            var handledSegmentIds = new HashSet<string>(
                allVisibleTracks
                    .Where(t => string.Equals(t.TrackType, TrackTypes.Effect, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(t => t.Segments ?? [])
                    .Select(s => s.Id),
                StringComparer.Ordinal);

            foreach (var element in ctx.Elements.OfType<VisualizerElement>())
            {
                if (!string.IsNullOrEmpty(element.SegmentId) && handledSegmentIds.Contains(element.SegmentId))
                    continue; // already processed above

                double maxEndTime = ResolveRenderDuration(ctx.Project);
                var vizLayer = await BuildSingleVisualizerLayerAsync(ctx, element, null, zOrder, resolvedAudioPath, ct);
                if (vizLayer != null) layers.Add(vizLayer);
            }
        }

        var totalDuration = ResolveRenderDuration(ctx.Project);

        return new CompositionPlan
        {
            RenderWidth = ctx.RenderWidth,
            RenderHeight = ctx.RenderHeight,
            FrameRate = ctx.FrameRate,
            PrimaryAudioPath = resolvedAudioPath,
            PrimaryAudioVolume = ctx.PrimaryAudioVolume,
            Layers = layers,
            AudioLayers = BuildAudioLayers(ctx.Project, assetMap),
            TotalDuration = totalDuration,
            OutputPath = ctx.OutputPath,
            VideoCodec = ctx.VideoCodec,
            AudioCodec = ctx.AudioCodec,
            Quality = ctx.Quality,
            AspectRatio = ctx.AspectRatio,
            ScaleMode = ctx.ScaleMode
        };
    }

    // ── Single-segment layer builders (called from unified loop) ──────

    private static CompositionLayer? BuildSingleVisualLayer(
        CompositionBuildContext ctx, Segment segment, Track track, ZOrderCounter zOrder,
        ElementSegmentRegistry registry, Dictionary<string, Asset> assetMap)
    {
        if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
            return null;

        if (!assetMap.TryGetValue(segment.BackgroundAssetId, out var asset)
            || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
        {
            Log.Warning("CompositionBuilder: skipping segment {SegId} — asset missing (AssetId={AssetId})",
                segment.Id, segment.BackgroundAssetId);
            return null;
        }

        var linkedElement = registry.GetElementForSegment(segment.Id);
        if (linkedElement is VisualizerElement)
            return null;

        var (overlayX, overlayY, scaleW, scaleH) = MapElementToRenderCoords(
            linkedElement, ctx.CanvasWidth, ctx.CanvasHeight, ctx.RenderWidth, ctx.RenderHeight);

        // Resolve effective overlay: segment override ?? track default
        var effectiveOverlayColor = segment.OverlayColorHex ?? track.OverlayColorHex;
        var effectiveOverlayOpacity = segment.OverlayOpacity ?? track.OverlayOpacity;

        return new CompositionLayer
        {
            ZOrder = zOrder.Next(),
            SourceType = IsVideoAsset(asset) ? CompositionSourceType.Video : CompositionSourceType.Image,
            SourcePath = asset.FilePath,
            StartTime = segment.StartTime,
            EndTime = segment.EndTime,
            OverlayX = overlayX,
            OverlayY = overlayY,
            ScaleWidth = scaleW,
            ScaleHeight = scaleH,
            OverlayColorHex = effectiveOverlayOpacity > 0 ? effectiveOverlayColor : null,
            OverlayOpacity = effectiveOverlayOpacity
        };
    }

    private static CompositionLayer? BuildSingleTextLayer(
        CompositionBuildContext ctx, Segment segment, ZOrderCounter zOrder,
        ElementSegmentRegistry registry, string textImageDir, ref int textIndex)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
            return null;

        var linkedElement = registry.GetElementForSegment(segment.Id);

        var options = new TextRasterizeOptions
        {
            Text = segment.Text,
            CanvasWidth = ctx.CanvasWidth,
            CanvasHeight = ctx.CanvasHeight
        };

        int overlayX = 0, overlayY = 0;
        int imgWidth = ctx.RenderWidth, imgHeight = 80;

        if (linkedElement != null && ctx.CanvasWidth > 0 && ctx.CanvasHeight > 0)
        {
            var scaleX = (double)ctx.RenderWidth / ctx.CanvasWidth;
            var scaleY = (double)ctx.RenderHeight / ctx.CanvasHeight;

            overlayX = (int)Math.Round(linkedElement.X * scaleX);
            overlayY = (int)Math.Round(linkedElement.Y * scaleY);
            imgWidth = Math.Max(1, (int)Math.Round(linkedElement.Width * scaleX));
            imgHeight = Math.Max(1, (int)Math.Round(linkedElement.Height * scaleY));

            overlayX = Math.Max(0, Math.Min(overlayX, ctx.RenderWidth - 1));
            overlayY = Math.Max(0, Math.Min(overlayY, ctx.RenderHeight - 1));

            if (linkedElement is TextOverlayElement overlay)
            {
                options.FontSize = (float)CoordinateMapper.ScaleFontSize(overlay.FontSize, ctx.CanvasHeight, ctx.RenderHeight);
                options.ColorHex = overlay.ColorHex;
                options.FontFamily = overlay.FontFamily;
                options.IsBold = overlay.IsBold;
                options.IsItalic = overlay.IsItalic;
                options.Alignment = overlay.Alignment switch
                {
                    TextAlignment.Left => TextRasterizeAlignment.Left,
                    TextAlignment.Right => TextRasterizeAlignment.Right,
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
            options.FontSize = CoordinateMapper.ScaleFontSize(24, ctx.CanvasHeight > 0 ? ctx.CanvasHeight : ctx.RenderHeight, ctx.RenderHeight);
            imgHeight = (int)(ctx.RenderHeight * 0.1);
            overlayY = (int)(ctx.RenderHeight * 0.8);
            overlayX = 0;
            imgWidth = ctx.RenderWidth;
        }

        options.Width = imgWidth;
        options.Height = imgHeight;

        var imagePath = System.IO.Path.Combine(textImageDir, $"text_{textIndex}.png");
        try
        {
            TextRasterizer.RenderToFile(options, imagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CompositionBuilder: failed to rasterize text segment {Index}", textIndex);
            textIndex++;
            return null;
        }

        var layer = new CompositionLayer
        {
            ZOrder = zOrder.Next(),
            SourceType = CompositionSourceType.RasterizedText,
            SourcePath = imagePath,
            StartTime = segment.StartTime,
            EndTime = segment.EndTime,
            HasAlpha = true,
            OverlayX = overlayX,
            OverlayY = overlayY,
            ScaleWidth = imgWidth,
            ScaleHeight = imgHeight
        };
        textIndex++;
        return layer;
    }

    private static async Task<CompositionLayer?> BuildSingleVisualizerLayerAsync(
        CompositionBuildContext ctx, VisualizerElement element, Segment? segment,
        ZOrderCounter zOrder, string audioFilePath, CancellationToken ct)
    {
        if (ctx.CanvasWidth <= 0 || ctx.CanvasHeight <= 0)
            return null;

        var ffmpegPath = FFmpegService.GetFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            Log.Warning("CompositionBuilder: FFmpeg path unknown, skipping visualizer bake");
            return null;
        }

        double maxEndTime = ResolveRenderDuration(ctx.Project);
        if (maxEndTime <= 0)
            return null;

        double vizStart = segment?.StartTime ?? 0;
        double vizEnd = segment?.EndTime ?? maxEndTime;
        if (vizEnd <= vizStart) return null;

        Log.Information("CompositionBuilder: baking VisualizerElement {Id} ({Style}) [{Start:F2}–{End:F2}s]",
            element.Id, element.Style, vizStart, vizEnd);

        var bakedPath = await OfflineVisualizerBaker.BakeAsync(
            element, audioFilePath,
            ctx.RenderWidth, ctx.RenderHeight,
            (int)ctx.CanvasWidth, (int)ctx.CanvasHeight,
            vizStart, vizEnd, ctx.FrameRate, ffmpegPath, ct);

        if (string.IsNullOrEmpty(bakedPath))
        {
            Log.Warning("CompositionBuilder: visualizer bake returned empty path for element {Id} ({Style}) — skipping layer",
                element.Id, element.Style);
            return null;
        }

        var scaleX = ctx.RenderWidth / ctx.CanvasWidth;
        var scaleY = ctx.RenderHeight / ctx.CanvasHeight;

        int overlayX = Math.Max(0, (int)Math.Round(element.X * scaleX));
        int overlayY = Math.Max(0, (int)Math.Round(element.Y * scaleY));
        int overlayW = Math.Max(1, (int)Math.Round(element.Width * scaleX));
        int overlayH = Math.Max(1, (int)Math.Round(element.Height * scaleY));

        return new CompositionLayer
        {
            ZOrder = zOrder.Next(),
            SourceType = CompositionSourceType.Visualizer,
            SourcePath = bakedPath,
            StartTime = vizStart,
            EndTime = vizEnd,
            HasAlpha = true,
            OverlayX = overlayX,
            OverlayY = overlayY,
            ScaleWidth = overlayW,
            ScaleHeight = overlayH
        };
    }

    private static List<CompositionAudioLayer> BuildAudioLayers(Project project, Dictionary<string, Asset> assetMap)
    {
        var audioTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderBy(t => t.Order)
            .ToList();

        if (audioTracks == null || audioTracks.Count == 0)
            return [];

        var layers = new List<CompositionAudioLayer>();
        foreach (var track in audioTracks)
        {
            if (track.Segments == null) continue;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (segment.EndTime <= segment.StartTime || string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                    continue;

                if (!assetMap.TryGetValue(segment.BackgroundAssetId, out var asset)
                    || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                    continue;

                layers.Add(new CompositionAudioLayer
                {
                    SourcePath = asset.FilePath,
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    Volume = segment.Volume,
                    FadeInDuration = segment.FadeInDuration,
                    FadeOutDuration = segment.FadeOutDuration,
                    SourceOffsetSeconds = segment.SourceStartOffset,
                    IsLooping = false
                });
            }
        }

        return layers;
    }

    private static (int overlayX, int overlayY, int scaleW, int scaleH) MapElementToRenderCoords(
        CanvasElement? element, double canvasWidth, double canvasHeight, int renderWidth, int renderHeight)
    {
        if (element == null || canvasWidth <= 0 || canvasHeight <= 0 || renderWidth <= 0 || renderHeight <= 0)
            return (0, 0, 0, 0);

        var scaleX = renderWidth / canvasWidth;
        var scaleY = renderHeight / canvasHeight;

        var overlayX = Math.Max(0, Math.Min((int)Math.Round(element.X * scaleX), renderWidth - 1));
        var overlayY = Math.Max(0, Math.Min((int)Math.Round(element.Y * scaleY), renderHeight - 1));
        var scaleW = Math.Max(1, Math.Min((int)Math.Round(element.Width * scaleX), renderWidth));
        var scaleH = Math.Max(1, Math.Min((int)Math.Round(element.Height * scaleY), renderHeight));

        return (overlayX, overlayY, scaleW, scaleH);
    }

    private static double ResolveRenderDuration(Project project)
    {
        var trackMax = project.Tracks?
            .SelectMany(t => t.Segments ?? [])
            .Select(s => s.EndTime)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        double audioMax = 0;
        if (!string.IsNullOrWhiteSpace(project.AudioPath) && System.IO.File.Exists(project.AudioPath))
        {
            try
            {
                using var reader = new NAudio.Wave.AudioFileReader(project.AudioPath);
                audioMax = reader.TotalTime.TotalSeconds;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CompositionBuilder: could not read audio duration");
            }
        }

        return Math.Max(trackMax, audioMax);
    }

    private static bool IsVideoAsset(Asset asset)
    {
        if (string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = System.IO.Path.GetExtension(asset.FilePath);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
