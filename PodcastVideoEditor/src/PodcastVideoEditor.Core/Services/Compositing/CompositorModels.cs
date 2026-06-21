#nullable enable
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// What a <see cref="CompositorLayer"/> draws. The compositor is the single source of truth
/// for both live preview and (eventually) export, so every visual primitive the timeline can
/// produce is represented here.
/// </summary>
public enum CompositorLayerKind
{
    /// <summary>Still image (also covers rasterized text PNG overlays).</summary>
    Image,
    /// <summary>Video clip frame sampled at the scene time.</summary>
    Video,
    /// <summary>Live audio-spectrum visualizer drawn directly (no PNG/MOV baking).</summary>
    Visualizer,
}

/// <summary>How a source image is fitted into its destination rectangle.</summary>
public enum CompositorScaleMode
{
    /// <summary>Cover the destination, preserving aspect ratio, cropping overflow.</summary>
    Fill,
    /// <summary>Contain inside the destination, preserving aspect ratio, letterboxing.</summary>
    Fit,
    /// <summary>Stretch to the destination exactly, ignoring aspect ratio.</summary>
    Stretch,
}

/// <summary>
/// A single drawable layer resolved for a specific scene time. Pure data — no SkiaSharp types —
/// so it is trivially unit-testable and reusable across preview and export backends.
/// All coordinates are in output (render) pixels.
/// </summary>
public sealed class CompositorLayer
{
    /// <summary>Cache key for the source texture (typically the source file path).</summary>
    public string SourceKey { get; init; } = string.Empty;

    public CompositorLayerKind Kind { get; init; } = CompositorLayerKind.Image;

    /// <summary>Z-order: lower draws first (further back).</summary>
    public int ZOrder { get; init; }

    /// <summary>Destination rectangle in output pixels.</summary>
    public float DestX { get; init; }
    public float DestY { get; init; }
    public float DestWidth { get; init; }
    public float DestHeight { get; init; }

    public CompositorScaleMode ScaleMode { get; init; } = CompositorScaleMode.Fill;

    /// <summary>Effective opacity for this frame in [0,1] (already includes fade in/out).</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Ken-Burns transform for this frame, relative to the destination rect centre.</summary>
    public double MotionScaleX { get; init; } = 1.0;
    public double MotionScaleY { get; init; } = 1.0;
    public double MotionTranslateX { get; init; }
    public double MotionTranslateY { get; init; }

    /// <summary>Optional solid colour tint drawn on top, hex (e.g. "#000000"). Null = none.</summary>
    public string? TintColorHex { get; init; }

    /// <summary>Tint opacity in [0,1]. 0 = disabled.</summary>
    public double TintOpacity { get; init; }

    /// <summary>True when the source carries its own alpha channel (e.g. visualizer/text PNG).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>Timeline start of the clip this layer belongs to (seconds). For video time mapping.</summary>
    public double ClipStartTime { get; init; }

    /// <summary>Offset into the source media where the clip begins (seconds). For video time mapping.</summary>
    public double SourceOffsetSeconds { get; init; }
}

/// <summary>
/// An immutable description of everything to draw for one output frame. Produced by
/// <see cref="CompositorSceneBuilder"/> and consumed by a renderer (Skia today, anything tomorrow).
/// </summary>
public sealed class CompositorScene
{
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }

    /// <summary>Scene time in seconds on the project timeline.</summary>
    public double TimeSeconds { get; init; }

    /// <summary>Layers in back-to-front draw order.</summary>
    public IReadOnlyList<CompositorLayer> Layers { get; init; } = new List<CompositorLayer>();
}
