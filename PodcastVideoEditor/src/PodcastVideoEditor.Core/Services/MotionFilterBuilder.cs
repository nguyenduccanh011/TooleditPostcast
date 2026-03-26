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

        var totalFrames = (int)Math.Ceiling(duration * fps);
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

    private static (string zExpr, string xExpr, string yExpr) BuildExpressions(
        string preset, double intensity, int totalFrames, CultureInfo inv)
    {
        // maxZoom: 1.0 + intensity * 0.4 → at intensity=0.3: 1.12x, at intensity=1.0: 1.4x
        var maxZoom = (1.0 + intensity * 0.4).ToString("F4", inv);
        // zoomSpeed per frame: how much to change zoom each frame to reach maxZoom over duration
        var zoomSpeed = (intensity * 0.4 / totalFrames).ToString("F6", inv);

        // panFraction: fraction of image to pan across (0.0-0.3 based on intensity)
        var panFraction = (intensity * 0.3).ToString("F4", inv);

        return preset switch
        {
            MotionPresets.ZoomIn => (
                zExpr: $"z='min(zoom+{zoomSpeed},{maxZoom})'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.ZoomOut => (
                zExpr: $"z='if(eq(on,1),{maxZoom},max(zoom-{zoomSpeed},1.0))'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanLeft => (
                zExpr: "z='1.1'",
                xExpr: $"x='iw*{panFraction}*(1-on/{totalFrames})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanRight => (
                zExpr: "z='1.1'",
                xExpr: $"x='iw*{panFraction}*(on/{totalFrames})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.PanUp => (
                zExpr: "z='1.1'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='ih*{panFraction}*(1-on/{totalFrames})'"
            ),

            MotionPresets.PanDown => (
                zExpr: "z='1.1'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='ih*{panFraction}*(on/{totalFrames})'"
            ),

            MotionPresets.ZoomInPanLeft => (
                zExpr: $"z='min(zoom+{zoomSpeed},{maxZoom})'",
                xExpr: $"x='iw*{panFraction}*(1-on/{totalFrames})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"
            ),

            MotionPresets.ZoomInPanRight => (
                zExpr: $"z='min(zoom+{zoomSpeed},{maxZoom})'",
                xExpr: $"x='iw*{panFraction}*(on/{totalFrames})'",
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
