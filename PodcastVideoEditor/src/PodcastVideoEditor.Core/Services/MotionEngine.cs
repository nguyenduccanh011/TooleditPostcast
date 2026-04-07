#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Globalization;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Shared motion math for both preview (WPF transforms) and render (FFmpeg expressions).
/// Keeps easing, zoom, and pan-travel formulas in one place to reduce WYSIWYG drift.
/// </summary>
public static class MotionEngine
{
    public static int ComputeTotalFrames(double durationSeconds, int fps) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, durationSeconds) * Math.Max(1, fps)));

    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return 0.5 - 0.5 * Math.Cos(Math.PI * t);
    }

    public static (double scaleX, double scaleY, double translateX, double translateY) ComputeTransform(
        string preset,
        double intensity,
        double progress,
        double width,
        double height,
        double durationSeconds,
        int fps)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset == MotionPresets.None)
            return (1.0, 1.0, 0.0, 0.0);

        intensity = Math.Clamp(intensity, 0.0, 1.0);
        progress = Math.Clamp(progress, 0.0, 1.0);

        var maxZoom = 1.0 + intensity * 0.4;
        var panScale = 1.0 + intensity * 0.3;
        var eased = Ease(progress);
        var totalFrames = ComputeTotalFrames(durationSeconds, fps);

        var travelX = ComputePanTravelPixels(width, intensity, totalFrames);
        var travelY = ComputePanTravelPixels(height, intensity, totalFrames);

        return preset switch
        {
            MotionPresets.ZoomIn => (
                Lerp(1.0, maxZoom, progress),
                Lerp(1.0, maxZoom, progress),
                0.0,
                0.0),

            MotionPresets.ZoomOut => (
                Lerp(maxZoom, 1.0, progress),
                Lerp(maxZoom, 1.0, progress),
                0.0,
                0.0),

            MotionPresets.PanLeft => (
                panScale,
                panScale,
                Lerp(-travelX, travelX, eased),
                0.0),

            MotionPresets.PanRight => (
                panScale,
                panScale,
                Lerp(travelX, -travelX, eased),
                0.0),

            MotionPresets.PanUp => (
                panScale,
                panScale,
                0.0,
                Lerp(-travelY, travelY, eased)),

            MotionPresets.PanDown => (
                panScale,
                panScale,
                0.0,
                Lerp(travelY, -travelY, eased)),

            MotionPresets.ZoomInPanLeft => (
                Lerp(1.0, maxZoom, progress),
                Lerp(1.0, maxZoom, progress),
                Lerp(-travelX, travelX, eased),
                0.0),

            MotionPresets.ZoomInPanRight => (
                Lerp(1.0, maxZoom, progress),
                Lerp(1.0, maxZoom, progress),
                Lerp(travelX, -travelX, eased),
                0.0),

            _ => (1.0, 1.0, 0.0, 0.0)
        };
    }

    public static (string zExpr, string xExpr, string yExpr) BuildZoomPanExpressions(
        string preset,
        double intensity,
        int totalFrames,
        int outW,
        int outH,
        CultureInfo inv)
    {
        intensity = Math.Clamp(intensity, 0.0, 1.0);
        totalFrames = Math.Max(1, totalFrames);

        var maxZoom = 1.0 + intensity * 0.4;
        var maxZoomStr = maxZoom.ToString("F4", inv);
        var zoomSpeed = (intensity * 0.4 / totalFrames).ToString("F6", inv);

        var panZoom = (1.0 + intensity * 0.3).ToString("F4", inv);
        var panFraction = (intensity * 0.3).ToString("F4", inv);

        var minPanX = Math.Clamp(totalFrames * (0.18 + intensity * 0.22), 12.0, outW * 0.12);
        var minPanY = Math.Clamp(totalFrames * (0.18 + intensity * 0.22), 12.0, outH * 0.12);
        var minPanXStr = minPanX.ToString("F4", inv);
        var minPanYStr = minPanY.ToString("F4", inv);

        var panTravelX = $"min(max(0,(iw-iw/zoom)),max({minPanXStr},max(0,(iw-iw/zoom))*{panFraction}))";
        var panTravelY = $"min(max(0,(ih-ih/zoom)),max({minPanYStr},max(0,(ih-ih/zoom))*{panFraction}))";

        var ease = $"(0.5-0.5*cos(PI*on/{totalFrames}.0))";

        return preset switch
        {
            MotionPresets.ZoomIn => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.ZoomOut => (
                zExpr: $"z='if(eq(on,1),{maxZoomStr},{maxZoomStr}-{zoomSpeed}*on)'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanLeft => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='{panTravelX}*(1-{ease})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanRight => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='{panTravelX}*{ease}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanUp => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='{panTravelY}*(1-{ease})'"),

            MotionPresets.PanDown => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='{panTravelY}*{ease}'"),

            MotionPresets.ZoomInPanLeft => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='{panTravelX}*(1-{ease})'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.ZoomInPanRight => (
                zExpr: $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='{panTravelX}*{ease}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            _ => (
                zExpr: "z='1'",
                xExpr: "x='0'",
                yExpr: "y='0'")
        };
    }

    private static double ComputePanTravelPixels(double axisSize, double intensity, int totalFrames)
    {
        var baseline = axisSize * intensity * 0.15;
        var minimum = Math.Clamp(totalFrames * (0.18 + intensity * 0.22), 12.0, axisSize * 0.12);
        return Math.Min(axisSize * 0.5, Math.Max(baseline, minimum));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
