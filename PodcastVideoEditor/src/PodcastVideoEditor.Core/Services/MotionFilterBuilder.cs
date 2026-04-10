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
    // For the supersample UPSCALE step before zoompan, bilinear is:
    //   (a) faster than lanczos (especially at 3x = 3240x3240 or 5400x5400)
    //   (b) better quality — lanczos for upscaling introduces ringing artifacts
    private const string MotionSuperSampleUpscaleFlags = "bilinear";
    private const double MotionTargetInternalPixelsPerFrame = 1.0;
    private const double MotionTargetInternalPixelsPerFrameShimmerBand = 1.35;
    // Min=1: allow skipping supersample for fast motion (≥1 px/frame already smooth)
    // Max=3: cap supersample at 3x (9x area). Was 6x (36x area) = 4x more CPU work.
    //        Difference in visual quality between 3x and 5x is imperceptible for video.
    private const int MotionSuperSampleMin = 1;
    private const int MotionSuperSampleMax = 3;

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

        var localDuration = seg.Duration;
        if (localDuration <= 0) return null;

        var referenceDuration = seg.MotionReferenceDurationSeconds > 0.0001
            ? Math.Max(seg.MotionReferenceDurationSeconds, localDuration)
            : localDuration;
        var referenceOffsetSeconds = Math.Clamp(
            seg.MotionReferenceOffsetSeconds,
            0,
            Math.Max(0, referenceDuration - (1.0 / Math.Max(1, fps))));

        var localFrames = MotionEngine.ComputeTotalFrames(localDuration, fps);
        if (localFrames <= 0) return null;

        var referenceFrames = MotionEngine.ComputeTotalFrames(referenceDuration, fps);
        var referenceOffsetFrames = Math.Max(0, referenceOffsetSeconds * Math.Max(1, fps));

        var intensity = Math.Clamp(seg.MotionIntensity, 0.0, 1.0);

        // Determine output size — use segment scale if available, otherwise render resolution.
        var outW = seg.ScaleWidth ?? renderWidth;
        var outH = seg.ScaleHeight ?? renderHeight;

        var estimatedPanPxPerFrameX = MotionEngine.EstimatePanPixelsPerFrame(referenceDuration, fps, outW, intensity);
        var estimatedPanPxPerFrameY = MotionEngine.EstimatePanPixelsPerFrame(referenceDuration, fps, outH, intensity);
        var estimatedZoomCenterPxPerFrameX = MotionEngine.EstimateZoomCenterShiftPixelsPerFrame(referenceDuration, fps, outW, intensity);
        var estimatedZoomCenterPxPerFrameY = MotionEngine.EstimateZoomCenterShiftPixelsPerFrame(referenceDuration, fps, outH, intensity);
        var estimatedSourcePxPerFrame = EstimateActiveMotionPixelsPerFrame(
            seg.MotionPreset,
            estimatedPanPxPerFrameX,
            estimatedPanPxPerFrameY,
            estimatedZoomCenterPxPerFrameX,
            estimatedZoomCenterPxPerFrameY);

        // Supersample motion output to reduce visible stair-stepping during pan/zoom,
        // then downscale with high-quality kernel.
        var estimatedStepHzSource = fps * estimatedSourcePxPerFrame;
        var motionSuperSample = DetermineMotionSupersample(estimatedSourcePxPerFrame, estimatedStepHzSource);
        var internalW = Math.Max(1, outW * motionSuperSample);
        var internalH = Math.Max(1, outH * motionSuperSample);
        var estimatedStepHzInternal = fps * estimatedSourcePxPerFrame * motionSuperSample;

        Log.Debug(
            "MotionDiagnostics: preset={Preset}, intensity={Intensity:F3}, localDuration={LocalDuration:F3}s, referenceDuration={ReferenceDuration:F3}s, referenceOffset={ReferenceOffset:F3}s, fps={Fps}, localFrames={LocalFrames}, referenceFrames={ReferenceFrames}, out={OutW}x{OutH}, internal={InternalW}x{InternalH}, supersample={Supersample}, estPanPxPerFrame=({PanX:F4},{PanY:F4}), estZoomCenterPxPerFrame=({ZoomX:F4},{ZoomY:F4}), estSourcePxPerFrame={SourcePx:F4}, estStepHzSource={StepHzSource:F2}, estStepHzInternal={StepHzInternal:F2}",
            seg.MotionPreset,
            intensity,
            localDuration,
            referenceDuration,
            referenceOffsetSeconds,
            fps,
            localFrames,
            referenceFrames,
            outW,
            outH,
            internalW,
            internalH,
            motionSuperSample,
            estimatedPanPxPerFrameX,
            estimatedPanPxPerFrameY,
            estimatedZoomCenterPxPerFrameX,
            estimatedZoomCenterPxPerFrameY,
            estimatedSourcePxPerFrame,
            estimatedStepHzSource,
            estimatedStepHzInternal);

        var estimatedInternalPxPerFrame = estimatedSourcePxPerFrame * motionSuperSample;
        if (estimatedInternalPxPerFrame < 1.0)
        {
            Log.Debug(
                "MotionFilterBuilder: motion quantization risk remains high (est source {SourcePx:F4} px/frame, supersample={SuperSample}, internal={InternalPx:F4} px/frame).",
                estimatedSourcePxPerFrame,
            motionSuperSample,
            estimatedInternalPxPerFrame);
        }

        if (IsShimmerBand(estimatedStepHzSource))
        {
            Log.Debug(
                "MotionFilterBuilder: estimated source step frequency is in visible shimmer band ({StepHzSource:F2} Hz). Internal supersampled step is {StepHzInternal:F2} Hz.",
                estimatedStepHzSource,
                estimatedStepHzInternal);
        }

        var inv = CultureInfo.InvariantCulture;

        // Compute zoom and pan expressions using shared motion engine
        var (zExpr, xExpr, yExpr) = MotionEngine.BuildZoomPanExpressions(
            seg.MotionPreset,
            intensity,
            referenceDuration,
            referenceFrames,
            outW,
            outH,
            inv,
            referenceOffsetFrames);

        var normalizeFilter = BuildMotionInputNormalizeFilter(outW, outH, seg.ScaleMode, MotionDownscaleFlags);
        // Use bilinear (not lanczos) for the supersample upscale step:
        // - We are UPSCALING from outW/outH to internalW/internalH before zoompan
        // - Lanczos is designed for downscaling; on upscaling it is slower + adds ringing
        // - Bilinear is faster and produces smoother result as zoompan input
        var supersampleInputFilter = motionSuperSample > 1
            ? $"scale={internalW}:{internalH}:flags={MotionSuperSampleUpscaleFlags}"
            : string.Empty;

        var supersamplePart = supersampleInputFilter.Length > 0 ? $",{supersampleInputFilter}" : string.Empty;

        return $"{normalizeFilter}{supersamplePart}," +
            $"zoompan={zExpr}:{xExpr}:{yExpr}:d={localFrames}:s={outW}x{outH}:fps={fps}," +
            $"format={outputPixelFormat}";
    }

    private static int DetermineMotionSupersample(double estimatedSourcePxPerFrame, double estimatedStepHzSource)
    {
        var safePx = Math.Max(0.0001, estimatedSourcePxPerFrame);
        var targetInternalPxPerFrame = IsShimmerBand(estimatedStepHzSource)
            ? MotionTargetInternalPixelsPerFrameShimmerBand
            : MotionTargetInternalPixelsPerFrame;
        var needed = (int)Math.Ceiling(targetInternalPxPerFrame / safePx);
        return Math.Clamp(needed, MotionSuperSampleMin, MotionSuperSampleMax);
    }

    private static bool IsShimmerBand(double hz) => hz >= 8.0 && hz <= 20.0;

    private static string BuildMotionInputNormalizeFilter(int width, int height, string? scaleMode, string flags)
    {
        var mode = (scaleMode ?? "Fill").ToUpperInvariant();
        return mode switch
        {
            "FIT" => $"scale={width}:{height}:flags={flags}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "STRETCH" => $"scale={width}:{height}:flags={flags}",
            _ => $"scale={width}:{height}:flags={flags}:force_original_aspect_ratio=increase,crop={width}:{height}"
        };
    }

    private static double EstimateActiveMotionPixelsPerFrame(
        string preset,
        double panX,
        double panY,
        double zoomCenterX,
        double zoomCenterY) => preset switch
    {
        MotionPresets.PanLeft or MotionPresets.PanRight => Math.Max(0.0001, panX),
        MotionPresets.PanUp or MotionPresets.PanDown => Math.Max(0.0001, panY),
        MotionPresets.ZoomIn or MotionPresets.ZoomOut => Math.Max(0.0001, MinPositive(zoomCenterX, zoomCenterY)),
        MotionPresets.ZoomInPanLeft or MotionPresets.ZoomInPanRight => Math.Max(0.0001, MinPositive(panX, zoomCenterX, zoomCenterY)),
        _ => Math.Max(0.0001, MinPositive(panX, panY, zoomCenterX, zoomCenterY))
    };

    private static double MinPositive(params double[] values)
    {
        var positives = values.Where(v => v > 0).ToArray();
        return positives.Length == 0 ? 0.0001 : positives.Min();
    }

}
