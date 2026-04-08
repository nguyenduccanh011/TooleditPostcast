#nullable enable
using PodcastVideoEditor.Core.Models;
using Serilog;
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
    private const string MotionDownscaleFlags = "lanczos+accurate_rnd+full_chroma_int";
    private const double MotionTargetInternalPixelsPerFrame = 1.0;
    private const int MotionSuperSampleMin = 2;
    private const int MotionSuperSampleMax = 4;

    /// <summary>
    /// Build the FFmpeg zoompan filter string for a visual segment.
    /// Returns null if the preset is None or the segment is a video.
    /// </summary>
    /// <param name="seg">The render segment with motion data.</param>
    /// <param name="fps">Output frame rate (e.g. 30).</param>
    /// <param name="renderWidth">Output video width in pixels.</param>
    /// <param name="renderHeight">Output video height in pixels.</param>
    /// <returns>FFmpeg filter expression like "zoompan=z=...:x=...:y=...:d=...:s=WxH:fps=30", or null.</returns>
    public static string? BuildZoompanFilter(RenderVisualSegment seg, int fps, int renderWidth, int renderHeight, string outputPixelFormat = "yuv420p")
    {
        if (seg.IsVideo) return null;
        if (seg.MotionPreset == MotionPresets.None) return null;

        var duration = seg.Duration;
        if (duration <= 0) return null;

        var totalFrames = MotionEngine.ComputeTotalFrames(duration, fps);
        if (totalFrames <= 0) return null;

        var intensity = Math.Clamp(seg.MotionIntensity, 0.0, 1.0);

        // Determine output size — use segment scale if available, otherwise render resolution.
        var outW = seg.ScaleWidth ?? renderWidth;
        var outH = seg.ScaleHeight ?? renderHeight;

        var estimatedPanPxPerFrameX = MotionEngine.EstimatePanPixelsPerFrame(duration, fps, outW, intensity);
        var estimatedPanPxPerFrameY = MotionEngine.EstimatePanPixelsPerFrame(duration, fps, outH, intensity);
        var estimatedZoomCenterPxPerFrameX = MotionEngine.EstimateZoomCenterShiftPixelsPerFrame(duration, fps, outW, intensity);
        var estimatedZoomCenterPxPerFrameY = MotionEngine.EstimateZoomCenterShiftPixelsPerFrame(duration, fps, outH, intensity);
        var estimatedSourcePxPerFrame = MinPositive(
            estimatedPanPxPerFrameX,
            estimatedPanPxPerFrameY,
            estimatedZoomCenterPxPerFrameX,
            estimatedZoomCenterPxPerFrameY);

        // Supersample motion output to reduce visible stair-stepping during pan/zoom,
        // then downscale with high-quality kernel.
        var motionSuperSample = DetermineMotionSupersample(estimatedSourcePxPerFrame);
        var internalW = Math.Max(1, outW * motionSuperSample);
        var internalH = Math.Max(1, outH * motionSuperSample);

        Log.Debug(
            "MotionFilterBuilder: preset={Preset}, intensity={Intensity:F3}, frames={Frames}, out={OutW}x{OutH}, internal={InternalW}x{InternalH}, supersample={Supersample}, estPanPxPerFrame=({PanX:F4},{PanY:F4}), estZoomCenterPxPerFrame=({ZoomX:F4},{ZoomY:F4}), estSourcePxPerFrame={SourcePx:F4}",
            seg.MotionPreset,
            intensity,
            totalFrames,
            outW,
            outH,
            internalW,
            internalH,
            motionSuperSample,
            estimatedPanPxPerFrameX,
            estimatedPanPxPerFrameY,
            estimatedZoomCenterPxPerFrameX,
            estimatedZoomCenterPxPerFrameY,
            estimatedSourcePxPerFrame);

        if (estimatedSourcePxPerFrame * motionSuperSample < 1.0)
        {
            Log.Warning(
                "MotionFilterBuilder: motion quantization risk remains high (est source {SourcePx:F4} px/frame, supersample={SuperSample}, internal={InternalPx:F4} px/frame).",
                estimatedSourcePxPerFrame,
                motionSuperSample);
        }

        var inv = CultureInfo.InvariantCulture;

        // Compute zoom and pan expressions using shared motion engine
        var (zExpr, xExpr, yExpr) = MotionEngine.BuildZoomPanExpressions(
            seg.MotionPreset,
            intensity,
            duration,
            totalFrames,
            outW,
            outH,
            inv);

         return $"zoompan={zExpr}:{xExpr}:{yExpr}:d={totalFrames}:s={internalW}x{internalH}:fps={fps}," +
             $"scale={outW}:{outH}:flags={MotionDownscaleFlags},format={outputPixelFormat}";
    }

    private static int DetermineMotionSupersample(double estimatedSourcePxPerFrame)
    {
        var safePx = Math.Max(0.0001, estimatedSourcePxPerFrame);
        var needed = (int)Math.Ceiling(MotionTargetInternalPixelsPerFrame / safePx);
        return Math.Clamp(needed, MotionSuperSampleMin, MotionSuperSampleMax);
    }

    private static double MinPositive(params double[] values)
    {
        var positives = values.Where(v => v > 0).ToArray();
        return positives.Length == 0 ? 0.0001 : positives.Min();
    }

}
