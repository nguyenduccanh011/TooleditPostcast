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

        var totalFrames = (int)Math.Round(duration * fps);
        if (totalFrames <= 0) return null;

        var intensity = Math.Clamp(seg.MotionIntensity, 0.0, 1.0);

        // Determine output size — use segment scale if available, otherwise render resolution
        var outW = seg.ScaleWidth ?? renderWidth;
        var outH = seg.ScaleHeight ?? renderHeight;

        var inv = CultureInfo.InvariantCulture;

        // Compute zoom and pan expressions based on preset
        var (zExpr, xExpr, yExpr) = BuildExpressions(seg.MotionPreset, intensity, totalFrames, inv);

        return $"zoompan={zExpr}:{xExpr}:{yExpr}:d={totalFrames}:s={outW}x{outH}:fps={fps}";
    }

    /// <summary>
    /// FFmpeg smoothstep easing expression: maps linear frame progress to a cosine ease-in-out curve.
    /// Result: 0 at on=0, 0.5 at mid-point, 1.0 at on=totalFrames.
    /// </summary>
    private static string EaseExpr(int totalFrames) =>
        $"(0.5-0.5*cos(PI*on/{totalFrames}.0))";

    private static (string zExpr, string xExpr, string yExpr) BuildExpressions(
        string preset, double intensity, int totalFrames, CultureInfo inv)
    {
        // maxZoom: 1.0 + intensity * 0.4 → at intensity=0.3: 1.12x, at intensity=1.0: 1.4x
        var maxZoom = 1.0 + intensity * 0.4;
        var maxZoomStr = maxZoom.ToString("F4", inv);
        // zoomSpeed per frame: deterministic increment to reach maxZoom over duration
        var zoomSpeed = (intensity * 0.4 / totalFrames).ToString("F6", inv);

        // panZoom: static zoom for pan presets — matches MotionAnimator panScale for consistency
        var panZoom = (1.0 + intensity * 0.3).ToString("F4", inv);

        // panFraction: fraction of image to pan across (0.0-0.3 based on intensity)
        var panFraction = (intensity * 0.3).ToString("F4", inv);

        // Eased progress: cosine smoothstep for buttery motion
        var ease = EaseExpr(totalFrames);

        return preset switch
        {
            MotionPresets.ZoomIn => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.ZoomOut => (
                zExpr: $"z='if(eq(on,1),{maxZoomStr},{maxZoomStr}-{zoomSpeed}*on)'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanLeft => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='iw*{panFraction}*(1-{ease})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanRight => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='iw*{panFraction}*{ease}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanUp => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='ih*{panFraction}*(1-{ease})'"
            ),

            MotionPresets.PanDown => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='ih*{panFraction}*{ease}'"
            ),

            MotionPresets.ZoomInPanLeft => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='iw*{panFraction}*(1-{ease})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.ZoomInPanRight => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='iw*{panFraction}*{ease}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            _ => (
                zExpr: "z='1'",
                xExpr: "x='0'",
                yExpr: "y='0'"
            )
        };
    }
}
