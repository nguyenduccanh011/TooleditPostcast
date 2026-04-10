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
    private const double DurationReferenceSeconds = 5.0;
    private const double MinDurationDamping = 0.30;

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

        var effectiveIntensity = ApplyDurationDamping(intensity, durationSeconds);

        var maxZoom = 1.0 + effectiveIntensity * 0.4;
        var panScale = 1.0 + effectiveIntensity * 0.3;
        var eased = Ease(progress);
        var totalFrames = ComputeTotalFrames(durationSeconds, fps);

        var travelX = ComputePanTravelPixels(width, effectiveIntensity, totalFrames);
        var travelY = ComputePanTravelPixels(height, effectiveIntensity, totalFrames);

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
        double durationSeconds,
        int totalFrames,
        int outW,
        int outH,
        CultureInfo inv,
        double startFrameOffset = 0)
    {
        intensity = Math.Clamp(intensity, 0.0, 1.0);
        totalFrames = Math.Max(1, totalFrames);

        var effectiveIntensity = ApplyDurationDamping(intensity, durationSeconds);

        var maxZoom = 1.0 + effectiveIntensity * 0.4;
        var maxZoomStr = maxZoom.ToString("F4", inv);
        var zoomSpeed = (effectiveIntensity * 0.4 / totalFrames).ToString("F6", inv);
        var startFrameOffsetStr = startFrameOffset.ToString("F4", inv);
        var frameExpr = startFrameOffset > 0.0001 ? $"(on+{startFrameOffsetStr})" : "on";

        var panZoom = (1.0 + effectiveIntensity * 0.3).ToString("F4", inv);

        const double edgeSafetyFraction = 0.02;
        var ease = $"(0.5-0.5*cos(PI*{frameExpr}/{totalFrames}.0))";
        var edgeSafetyStr = edgeSafetyFraction.ToString("F4", inv);
        var motionSpanExpr = $"(1-2*{edgeSafetyStr})";
        var txRight = $"({edgeSafetyStr}+{motionSpanExpr}*{ease})";
        var txLeft = $"({edgeSafetyStr}+{motionSpanExpr}*(1-{ease}))";
        var tyDown = txRight;
        var tyUp = txLeft;
        var xRange = "max(0,(iw-iw/zoom))";
        var yRange = "max(0,(ih-ih/zoom))";

        return preset switch
        {
            MotionPresets.ZoomIn => (
                zExpr: startFrameOffset > 0.0001
                    ? $"z='1.0+{zoomSpeed}*{frameExpr}'"
                    : $"z='1.0+{zoomSpeed}*on'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.ZoomOut => (
                zExpr: startFrameOffset > 0.0001
                    ? $"z='{maxZoomStr}-{zoomSpeed}*{frameExpr}'"
                    : $"z='if(eq(on,1),{maxZoomStr},{maxZoomStr}-{zoomSpeed}*on)'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanLeft => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='{xRange}*{txLeft}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanRight => (
                zExpr: $"z='{panZoom}'",
                xExpr: $"x='{xRange}*{txRight}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.PanUp => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='{yRange}*{tyUp}'"),

            MotionPresets.PanDown => (
                zExpr: $"z='{panZoom}'",
                xExpr: "x='iw/2-(iw/zoom/2)'",
                yExpr: $"y='{yRange}*{tyDown}'"),

            MotionPresets.ZoomInPanLeft => (
                zExpr: startFrameOffset > 0.0001
                    ? $"z='1.0+{zoomSpeed}*{frameExpr}'"
                    : $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='{xRange}*{txLeft}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            MotionPresets.ZoomInPanRight => (
                zExpr: startFrameOffset > 0.0001
                    ? $"z='1.0+{zoomSpeed}*{frameExpr}'"
                    : $"z='1.0+{zoomSpeed}*on'",
                xExpr: $"x='{xRange}*{txRight}'",
                yExpr: "y='ih/2-(ih/zoom/2)'"),

            _ => (
                zExpr: "z='1'",
                xExpr: "x='0'",
                yExpr: "y='0'")
        };
    }

    public static double EstimatePanPixelsPerFrame(double durationSeconds, int fps, int axisSize, double intensity)
    {
        var totalFrames = ComputeTotalFrames(durationSeconds, fps);
        var effectiveIntensity = ApplyDurationDamping(Math.Clamp(intensity, 0.0, 1.0), durationSeconds);
        var travel = ComputePanTravelPixels(axisSize, effectiveIntensity, totalFrames);
        return totalFrames <= 1 ? travel : travel / totalFrames;
    }

    public static double EstimateZoomCenterShiftPixelsPerFrame(double durationSeconds, int fps, int axisSize, double intensity)
    {
        var totalFrames = ComputeTotalFrames(durationSeconds, fps);
        var effectiveIntensity = ApplyDurationDamping(Math.Clamp(intensity, 0.0, 1.0), durationSeconds);
        var maxZoom = 1.0 + effectiveIntensity * 0.4;
        var centerShiftTravel = axisSize * (1.0 - (1.0 / maxZoom)) * 0.5;
        return totalFrames <= 1 ? centerShiftTravel : centerShiftTravel / totalFrames;
    }

    private static double ComputePanTravelPixels(double axisSize, double intensity, int totalFrames)
    {
        var baseline = axisSize * intensity * 0.15;
        var minimum = Math.Clamp(totalFrames * (0.18 + intensity * 0.22), 12.0, axisSize * 0.12);
        var travel = Math.Min(axisSize * 0.5, Math.Max(baseline, minimum));

        // Keep per-frame pan velocity bounded to reduce visible pulse/jitter on very short segments.
        var maxPixelsPerFrame = 0.6 + 0.9 * intensity;
        var maxTravelForDuration = maxPixelsPerFrame * Math.Max(1, totalFrames);
        return Math.Min(travel, maxTravelForDuration);
    }

    private static double ApplyDurationDamping(double intensity, double durationSeconds)
    {
        var normalized = Math.Clamp(durationSeconds / DurationReferenceSeconds, 0.0, 1.0);
        var damping = MinDurationDamping + (1.0 - MinDurationDamping) * normalized;
        return Math.Clamp(intensity * damping, 0.0, 1.0);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
