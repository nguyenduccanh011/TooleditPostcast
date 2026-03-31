using PodcastVideoEditor.Core.Models;
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
        double elementWidth, double elementHeight)
    {
        if (string.IsNullOrEmpty(preset) || preset == MotionPresets.None || intensity <= 0)
            return MotionTransform.Identity;

        progress = Math.Clamp(progress, 0.0, 1.0);
        intensity = Math.Clamp(intensity, 0.0, 1.0);

        // maxZoom matches FFmpeg MotionFilterBuilder: 1.0 + intensity * 0.4
        var maxZoom = 1.0 + intensity * 0.4;

        // Pan presets use a slight constant zoom to provide room for translation.
        // The zoom provides overflow that gets clipped by the container.
        var panScale = 1.0 + intensity * 0.3;

        // Pan range: how far the image can translate within the clipped area.
        // With RenderTransformOrigin (0.5, 0.5), the excess on each side is:
        //   elementSize * (scale - 1) / 2
        // We use a fraction of that for the actual pan distance.
        var panRangeX = elementWidth * intensity * 0.15;
        var panRangeY = elementHeight * intensity * 0.15;

        return preset switch
        {
            // Zoom in toward center: scale grows from 1.0 to maxZoom
            MotionPresets.ZoomIn => new MotionTransform
            {
                ScaleX = Lerp(1.0, maxZoom, progress),
                ScaleY = Lerp(1.0, maxZoom, progress),
                TranslateX = 0,
                TranslateY = 0
            },

            // Zoom out from center: scale shrinks from maxZoom to 1.0
            MotionPresets.ZoomOut => new MotionTransform
            {
                ScaleX = Lerp(maxZoom, 1.0, progress),
                ScaleY = Lerp(maxZoom, 1.0, progress),
                TranslateX = 0,
                TranslateY = 0
            },

            // Pan left: camera moves right-to-left (image shifts right over time)
            MotionPresets.PanLeft => new MotionTransform
            {
                ScaleX = panScale,
                ScaleY = panScale,
                TranslateX = Lerp(-panRangeX, panRangeX, Ease(progress)),
                TranslateY = 0
            },

            // Pan right: camera moves left-to-right (image shifts left over time)
            MotionPresets.PanRight => new MotionTransform
            {
                ScaleX = panScale,
                ScaleY = panScale,
                TranslateX = Lerp(panRangeX, -panRangeX, Ease(progress)),
                TranslateY = 0
            },

            // Pan up: camera moves bottom-to-top (image shifts down over time)
            MotionPresets.PanUp => new MotionTransform
            {
                ScaleX = panScale,
                ScaleY = panScale,
                TranslateX = 0,
                TranslateY = Lerp(-panRangeY, panRangeY, Ease(progress))
            },

            // Pan down: camera moves top-to-bottom (image shifts up over time)
            MotionPresets.PanDown => new MotionTransform
            {
                ScaleX = panScale,
                ScaleY = panScale,
                TranslateX = 0,
                TranslateY = Lerp(panRangeY, -panRangeY, Ease(progress))
            },

            // Zoom in + pan left: combines zoom-in with leftward camera movement
            MotionPresets.ZoomInPanLeft => new MotionTransform
            {
                ScaleX = Lerp(1.0, maxZoom, progress),
                ScaleY = Lerp(1.0, maxZoom, progress),
                TranslateX = Lerp(-panRangeX, panRangeX, Ease(progress)),
                TranslateY = 0
            },

            // Zoom in + pan right: combines zoom-in with rightward camera movement
            MotionPresets.ZoomInPanRight => new MotionTransform
            {
                ScaleX = Lerp(1.0, maxZoom, progress),
                ScaleY = Lerp(1.0, maxZoom, progress),
                TranslateX = Lerp(panRangeX, -panRangeX, Ease(progress)),
                TranslateY = 0
            },

            _ => MotionTransform.Identity
        };
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Cosine ease-in-out: smooth acceleration/deceleration matching FFmpeg render.</summary>
    private static double Ease(double t) => 0.5 - 0.5 * Math.Cos(Math.PI * t);
}
