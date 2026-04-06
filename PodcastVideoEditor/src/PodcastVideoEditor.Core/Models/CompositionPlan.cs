#nullable enable
#pragma warning disable CS0618 // RenderConfig.TextSegments is intentionally kept for legacy compatibility paths.
using System;
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Source type for a composition layer.
/// </summary>
public enum CompositionSourceType
{
    Image,
    Video,
    Visualizer,
    RasterizedText
}

/// <summary>
/// A single composable layer in a render composition.
/// Represents one visual element with explicit z-order, time range, and spatial placement.
/// </summary>
public class CompositionLayer
{
    /// <summary>Explicit z-order (higher = rendered on top). Determines FFmpeg overlay stacking.</summary>
    public int ZOrder { get; init; }

    /// <summary>Source media type.</summary>
    public CompositionSourceType SourceType { get; init; }

    /// <summary>Path to the source file (image, video, baked visualizer .mov, rasterized text .png).</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>When the layer appears in the output timeline (seconds).</summary>
    public double StartTime { get; init; }

    /// <summary>When the layer disappears from the output timeline (seconds).</summary>
    public double EndTime { get; init; }

    /// <summary>Offset into the source file (seconds). For video trimming.</summary>
    public double SourceOffsetSeconds { get; init; }

    /// <summary>Overlay X position in render coordinates (pixels).</summary>
    public int OverlayX { get; init; }

    /// <summary>Overlay Y position in render coordinates (pixels).</summary>
    public int OverlayY { get; init; }

    /// <summary>Scaled width in render coordinates (pixels). 0 = use full render width.</summary>
    public int ScaleWidth { get; init; }

    /// <summary>Scaled height in render coordinates (pixels). 0 = use full render height.</summary>
    public int ScaleHeight { get; init; }

    /// <summary>Whether the source has an alpha channel (e.g. baked visualizer, rasterized text PNG).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>Scaling behavior (Fill, Fit, Stretch).</summary>
    public string ScaleMode { get; init; } = "Fill";

    /// <summary>Overlay color hex (e.g. "#000000") to tint this layer. Null = no overlay.</summary>
    public string? OverlayColorHex { get; init; }

    /// <summary>Overlay opacity (0.0–1.0). 0.0 = disabled. Applied as a color tint on top of the image.</summary>
    public double OverlayOpacity { get; init; }

    /// <summary>Duration of the layer in seconds.</summary>
    public double Duration => EndTime - StartTime;

    /// <summary>Whether this layer is a video source (as opposed to a still image).</summary>
    public bool IsVideo => SourceType is CompositionSourceType.Video or CompositionSourceType.Visualizer;
}

/// <summary>
/// An audio layer in a render composition.
/// </summary>
public class CompositionAudioLayer
{
    /// <summary>Path to the audio file.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>When the audio starts in the output timeline (seconds).</summary>
    public double StartTime { get; init; }

    /// <summary>When the audio ends in the output timeline (seconds).</summary>
    public double EndTime { get; init; }

    /// <summary>Volume multiplier (0.0–1.0).</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>Fade-in duration from StartTime (seconds).</summary>
    public double FadeInDuration { get; init; }

    /// <summary>Fade-out duration before EndTime (seconds).</summary>
    public double FadeOutDuration { get; init; }

    /// <summary>Offset into the source file where playback begins (seconds).</summary>
    public double SourceOffsetSeconds { get; init; }

    /// <summary>Whether the audio should loop to fill its time window.</summary>
    public bool IsLooping { get; init; }
}

/// <summary>
/// Complete render composition plan — an intermediate representation between the
/// project model and FFmpeg command generation. Provides a validated, ordered,
/// and explicit description of what the render output should contain.
///
/// Commercial editors (DaVinci Resolve, Premiere) use a similar intermediate
/// representation (node graph / composition tree) to decouple timeline editing
/// from render execution.
/// </summary>
public class CompositionPlan
{
    /// <summary>Output resolution width (pixels).</summary>
    public int RenderWidth { get; init; }

    /// <summary>Output resolution height (pixels).</summary>
    public int RenderHeight { get; init; }

    /// <summary>Target frame rate.</summary>
    public int FrameRate { get; init; } = 30;

    /// <summary>Path to the primary audio track (project narration).</summary>
    public string PrimaryAudioPath { get; init; } = string.Empty;

    /// <summary>Volume for the primary audio track.</summary>
    public double PrimaryAudioVolume { get; init; } = 1.0;

    /// <summary>
    /// Visual layers sorted by z-order (ascending). FFmpeg overlays are applied in this order.
    /// </summary>
    public List<CompositionLayer> Layers { get; init; } = [];

    /// <summary>Additional audio layers (BGM, SFX, etc.).</summary>
    public List<CompositionAudioLayer> AudioLayers { get; init; } = [];

    /// <summary>Total duration of the composition (seconds).</summary>
    public double TotalDuration { get; init; }

    /// <summary>Output file path.</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>Video codec (h264, h265, vp9).</summary>
    public string VideoCodec { get; init; } = "h264";

    /// <summary>Audio codec (aac, libmp3lame, opus).</summary>
    public string AudioCodec { get; init; } = "aac";

    /// <summary>Quality setting (Low, Medium, High).</summary>
    public string Quality { get; init; } = "Medium";

    /// <summary>Aspect ratio string (e.g. "9:16").</summary>
    public string AspectRatio { get; init; } = "9:16";

    /// <summary>Global scale mode (Fill, Fit, Stretch).</summary>
    public string ScaleMode { get; init; } = "Fill";

    /// <summary>
    /// Convert this plan to a legacy <see cref="RenderConfig"/> for backward compatibility
    /// with the existing FFmpegService pipeline.
    /// </summary>
    public RenderConfig ToRenderConfig()
    {
        var config = new RenderConfig
        {
            AudioPath = PrimaryAudioPath,
            OutputPath = OutputPath,
            ResolutionWidth = RenderWidth,
            ResolutionHeight = RenderHeight,
            AspectRatio = AspectRatio,
            Quality = Quality,
            FrameRate = FrameRate,
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            ScaleMode = ScaleMode,
            PrimaryAudioVolume = PrimaryAudioVolume,
            TextSegments = [],  // Text is rasterized to PNG (WYSIWYG)
        };

        // Convert layers to visual segments (ordered by z-order)
        foreach (var layer in Layers)
        {
            config.VisualSegments.Add(new RenderVisualSegment
            {
                SourcePath = layer.SourcePath,
                StartTime = layer.StartTime,
                EndTime = layer.EndTime,
                IsVideo = layer.IsVideo,
                HasAlpha = layer.HasAlpha,
                SourceOffsetSeconds = layer.SourceOffsetSeconds,
                OverlayX = layer.OverlayX > 0 || layer.ScaleWidth > 0 ? layer.OverlayX.ToString() : null,
                OverlayY = layer.OverlayY > 0 || layer.ScaleHeight > 0 ? layer.OverlayY.ToString() : null,
                ScaleWidth = layer.ScaleWidth > 0 ? layer.ScaleWidth : null,
                ScaleHeight = layer.ScaleHeight > 0 ? layer.ScaleHeight : null,
                ScaleMode = layer.ScaleMode,
                ZOrder = layer.ZOrder,
                OverlayColorHex = layer.OverlayColorHex,
                OverlayOpacity = layer.OverlayOpacity
            });
        }

        // Convert audio layers
        foreach (var audio in AudioLayers)
        {
            config.AudioSegments.Add(new RenderAudioSegment
            {
                SourcePath = audio.SourcePath,
                StartTime = audio.StartTime,
                EndTime = audio.EndTime,
                Volume = audio.Volume,
                FadeInDuration = audio.FadeInDuration,
                FadeOutDuration = audio.FadeOutDuration,
                SourceOffsetSeconds = audio.SourceOffsetSeconds,
                IsLooping = audio.IsLooping
            });
        }

        return config;
    }
}
#pragma warning restore CS0618
