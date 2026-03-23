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

        // Layer 1: Visual segments (images/videos from timeline tracks) — lowest z
        var visualLayers = BuildVisualLayers(ctx, zOrder, registry);
        layers.AddRange(visualLayers);

        // Layer 2: Rasterized text overlays — above visuals
        var textLayers = BuildRasterizedTextLayers(ctx, zOrder, registry);
        layers.AddRange(textLayers);

        // Layer 3: Baked visualizer spectrum overlays — on top
        var visualizerLayers = await BuildVisualizerLayersAsync(ctx, zOrder, registry, ct);
        layers.AddRange(visualizerLayers);

        var totalDuration = ResolveRenderDuration(ctx.Project);

        return new CompositionPlan
        {
            RenderWidth = ctx.RenderWidth,
            RenderHeight = ctx.RenderHeight,
            FrameRate = ctx.FrameRate,
            PrimaryAudioPath = ctx.Project.AudioPath ?? string.Empty,
            PrimaryAudioVolume = ctx.PrimaryAudioVolume,
            Layers = layers,
            AudioLayers = BuildAudioLayers(ctx.Project),
            TotalDuration = totalDuration,
            OutputPath = ctx.OutputPath,
            VideoCodec = ctx.VideoCodec,
            AudioCodec = ctx.AudioCodec,
            Quality = ctx.Quality,
            AspectRatio = ctx.AspectRatio,
            ScaleMode = ctx.ScaleMode
        };
    }

    private static List<CompositionLayer> BuildVisualLayers(CompositionBuildContext ctx, ZOrderCounter zOrder, ElementSegmentRegistry registry)
    {
        var visualTracks = ctx.Project.Tracks?
            .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderBy(t => t.Order)
            .ToList();

        if (visualTracks == null || visualTracks.Count == 0)
            return [];

        var layers = new List<CompositionLayer>();
        foreach (var track in visualTracks)
        {
            if (track.Segments == null) continue;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId) || segment.EndTime <= segment.StartTime)
                    continue;

                var asset = ctx.Project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                {
                    Log.Warning("CompositionBuilder: skipping segment {SegId} — asset missing (AssetId={AssetId})",
                        segment.Id, segment.BackgroundAssetId);
                    continue;
                }

                // O(1) lookup via registry — skip if it's a VisualizerElement (handled separately)
                var linkedElement = registry.GetElementForSegment(segment.Id);

                if (linkedElement is VisualizerElement)
                    continue;

                var (overlayX, overlayY, scaleW, scaleH) = MapElementToRenderCoords(
                    linkedElement, ctx.CanvasWidth, ctx.CanvasHeight, ctx.RenderWidth, ctx.RenderHeight);

                layers.Add(new CompositionLayer
                {
                    ZOrder = zOrder.Next(),
                    SourceType = IsVideoAsset(asset) ? CompositionSourceType.Video : CompositionSourceType.Image,
                    SourcePath = asset.FilePath,
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    OverlayX = overlayX,
                    OverlayY = overlayY,
                    ScaleWidth = scaleW,
                    ScaleHeight = scaleH
                });
            }
        }

        return layers;
    }

    private static List<CompositionLayer> BuildRasterizedTextLayers(CompositionBuildContext ctx, ZOrderCounter zOrder, ElementSegmentRegistry registry)
    {
        var textTracks = ctx.Project.Tracks?
            .Where(t => string.Equals(t.TrackType, "text", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
            .OrderBy(t => t.Order)
            .ToList();

        if (textTracks == null || textTracks.Count == 0)
            return [];

        var textImageDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PodcastVideoEditor", "render_text_img");
        System.IO.Directory.CreateDirectory(textImageDir);

        var layers = new List<CompositionLayer>();
        var index = 0;
        foreach (var track in textTracks)
        {
            if (track.Segments == null) continue;
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.Text) || segment.EndTime <= segment.StartTime)
                    continue;

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

                    if (linkedElement is TitleElement title)
                    {
                        options.FontSize = (float)CoordinateMapper.ScaleFontSize(title.FontSize, ctx.CanvasHeight, ctx.RenderHeight);
                        options.ColorHex = title.ColorHex;
                        options.FontFamily = title.FontFamily;
                        options.IsBold = title.IsBold;
                        options.IsItalic = title.IsItalic;
                        options.Alignment = title.Alignment switch
                        {
                            TextAlignment.Left => TextRasterizeAlignment.Left,
                            TextAlignment.Right => TextRasterizeAlignment.Right,
                            _ => TextRasterizeAlignment.Center
                        };
                    }
                    else if (linkedElement is TextElement text)
                    {
                        options.FontSize = (float)CoordinateMapper.ScaleFontSize(text.FontSize, ctx.CanvasHeight, ctx.RenderHeight);
                        options.ColorHex = text.ColorHex;
                        options.Alignment = TextRasterizeAlignment.Center;
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

                var imagePath = System.IO.Path.Combine(textImageDir, $"text_{index}.png");
                try
                {
                    TextRasterizer.RenderToFile(options, imagePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "CompositionBuilder: failed to rasterize text segment {Index}", index);
                    index++;
                    continue;
                }

                layers.Add(new CompositionLayer
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
                });
                index++;
            }
        }

        return layers;
    }

    private static async Task<List<CompositionLayer>> BuildVisualizerLayersAsync(
        CompositionBuildContext ctx, ZOrderCounter zOrder, ElementSegmentRegistry registry, CancellationToken ct)
    {
        if (ctx.Elements == null || ctx.CanvasWidth <= 0 || ctx.CanvasHeight <= 0)
            return [];

        var ffmpegPath = FFmpegService.GetFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            Log.Warning("CompositionBuilder: FFmpeg path unknown, skipping visualizer bake");
            return [];
        }

        double maxEndTime = ResolveRenderDuration(ctx.Project);
        if (maxEndTime <= 0)
            return [];

        var audioFilePath = ctx.Project.AudioPath ?? string.Empty;
        var layers = new List<CompositionLayer>();
        var scaleX = ctx.RenderWidth / ctx.CanvasWidth;
        var scaleY = ctx.RenderHeight / ctx.CanvasHeight;

        foreach (var element in ctx.Elements.OfType<VisualizerElement>())
        {
            double vizStart = 0;
            double vizEnd = maxEndTime;

            if (!string.IsNullOrEmpty(element.SegmentId))
            {
                // O(1) lookup via registry
                var linked = registry.GetSegmentById(element.SegmentId);
                if (linked != null)
                {
                    vizStart = linked.StartTime;
                    vizEnd = linked.EndTime;
                }
            }

            if (vizEnd <= vizStart) continue;

            Log.Information("CompositionBuilder: baking VisualizerElement {Id} ({Style}) [{Start:F2}–{End:F2}s]",
                element.Id, element.Style, vizStart, vizEnd);

            var bakedPath = await OfflineVisualizerBaker.BakeAsync(
                element, audioFilePath,
                ctx.RenderWidth, ctx.RenderHeight,
                (int)ctx.CanvasWidth, (int)ctx.CanvasHeight,
                vizStart, vizEnd, ctx.FrameRate, ffmpegPath, ct);

            if (string.IsNullOrEmpty(bakedPath)) continue;

            int overlayX = Math.Max(0, (int)Math.Round(element.X * scaleX));
            int overlayY = Math.Max(0, (int)Math.Round(element.Y * scaleY));
            int overlayW = Math.Max(1, (int)Math.Round(element.Width * scaleX));
            int overlayH = Math.Max(1, (int)Math.Round(element.Height * scaleY));

            layers.Add(new CompositionLayer
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
            });
        }

        return layers;
    }

    private static List<CompositionAudioLayer> BuildAudioLayers(Project project)
    {
        var audioTracks = project.Tracks?
            .Where(t => string.Equals(t.TrackType, "audio", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
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

                var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
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
