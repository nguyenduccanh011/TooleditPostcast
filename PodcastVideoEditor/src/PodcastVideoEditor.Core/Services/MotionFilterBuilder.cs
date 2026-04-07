#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Globalization;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Generates FFmpeg zoompan filter expressions for Ken Burns-style motion effects.
/// The zoompan filter animates a virtual camera over a still image, producing
/// smooth zoom and pan effects over the segment's duration.
///
/// FFmpeg zoompan filter syntax:
///   zoompan=z='expr':x='expr':y='expr':d=frames:s=WxH:fps=fps
///   - z: zoom factor expression (1.0 = no zoom)
///   - x, y: pan position expressions (top-left of the visible window)
///   - d: total duration in frames
///   - s: output size (matches render resolution)
///   - fps: output frame rate
///
/// Smoothness techniques:
///   - Deterministic formulas (1.0 + speed*on) avoid floating-point drift from accumulation.
///   - Cosine easing (0.5 - 0.5*cos(PI*on/N)) produces smooth acceleration/deceleration.
///   - Float-forced denominators (N.0) prevent FFmpeg integer division in pan expressions.
/// </summary>
public static class MotionFilterBuilder
{
    /// <summary>
    /// Build the FFmpeg zoompan filter string for a visual segment.
    /// Returns null if the preset is None or the segment is a video.
    /// </summary>
    /// <param name="seg">The render segment with motion data.</param>
    /// <param name="fps">Output frame rate (e.g. 30).</param>
    /// <param name="renderWidth">Output video width in pixels.</param>
    /// <param name="renderHeight">Output video height in pixels.</param>
    /// <returns>FFmpeg filter expression like "zoompan=z=...:x=...:y=...:d=...:s=WxH:fps=30", or null.</returns>
    public static string? BuildZoompanFilter(RenderVisualSegment seg, int fps, int renderWidth, int renderHeight)
    {
        if (seg.IsVideo) return null;
        if (seg.MotionPreset == MotionPresets.None) return null;

        var duration = seg.Duration;
        if (duration <= 0) return null;

        var totalFrames = MotionEngine.ComputeTotalFrames(duration, fps);
        if (totalFrames <= 0) return null;

        var intensity = Math.Clamp(seg.MotionIntensity, 0.0, 1.0);

        // Determine output size — use segment scale if available, otherwise render resolution
        var outW = seg.ScaleWidth ?? renderWidth;
        var outH = seg.ScaleHeight ?? renderHeight;

        var inv = CultureInfo.InvariantCulture;

        // Compute zoom and pan expressions using shared motion engine
        var (zExpr, xExpr, yExpr) = MotionEngine.BuildZoomPanExpressions(
            seg.MotionPreset,
            intensity,
            totalFrames,
            outW,
            outH,
            inv);

        return $"zoompan={zExpr}:{xExpr}:{yExpr}:d={totalFrames}:s={outW}x{outH}:fps={fps}";
    }
}
