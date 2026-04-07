using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using System;

namespace PodcastVideoEditor.Ui.Helpers;

/// <summary>
/// Computes WPF transform values for Ken Burns motion preview on the canvas.
/// Mirrors the FFmpeg zoompan behavior from MotionFilterBuilder but outputs
/// ScaleTransform + TranslateTransform parameters suitable for WPF RenderTransform.
///
/// This is a stateless, frame-by-frame calculator: given the current progress
/// within a segment (0.0–1.0), it returns the exact transforms for that instant.
/// This approach works perfectly with scrubbing — no Storyboard synchronization needed.
/// </summary>
public static class MotionAnimator
{
    /// <summary>
    /// Computed transform values for a single frame of motion preview.
    /// </summary>
    public readonly struct MotionTransform
    {
        public double ScaleX { get; init; }
        public double ScaleY { get; init; }
        public double TranslateX { get; init; }
        public double TranslateY { get; init; }

        public static readonly MotionTransform Identity = new()
        {
            ScaleX = 1.0,
            ScaleY = 1.0,
            TranslateX = 0,
            TranslateY = 0
        };
    }

    /// <summary>
    /// Compute the WPF transform for a motion preset at a given progress point.
    /// </summary>
    /// <param name="preset">Motion preset name (from MotionPresets constants).</param>
    /// <param name="intensity">Motion intensity 0.0–1.0.</param>
    /// <param name="progress">Progress within segment 0.0–1.0 (playhead-based).</param>
    /// <param name="elementWidth">Element width in pixels (for pan range calculation).</param>
    /// <param name="elementHeight">Element height in pixels (for pan range calculation).</param>
    /// <returns>Transform values to apply to the Image RenderTransform.</returns>
    public static MotionTransform Compute(
        string preset, double intensity, double progress,
        double elementWidth, double elementHeight,
        double durationSeconds,
        int fps)
    {
        if (string.IsNullOrEmpty(preset) || preset == MotionPresets.None || intensity <= 0)
            return MotionTransform.Identity;

        var (scaleX, scaleY, translateX, translateY) = MotionEngine.ComputeTransform(
            preset,
            intensity,
            progress,
            elementWidth,
            elementHeight,
            durationSeconds,
            fps);

        return new MotionTransform
        {
            ScaleX = scaleX,
            ScaleY = scaleY,
            TranslateX = translateX,
            TranslateY = translateY
        };
    }
}
