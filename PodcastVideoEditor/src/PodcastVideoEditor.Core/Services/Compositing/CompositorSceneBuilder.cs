#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// Builds a <see cref="CompositorScene"/> for an arbitrary timeline time from the same
/// <see cref="RenderVisualSegment"/> list the FFmpeg renderer consumes. This is the bridge
/// that lets the GPU compositor be WYSIWYG with export: it reuses <see cref="MotionEngine"/>
/// for Ken-Burns and the identical fade/tint/z-order rules.
///
/// Pure logic (no SkiaSharp / no I/O) so it is fully unit-testable.
/// </summary>
public static class CompositorSceneBuilder
{
    private const double Epsilon = 1e-6;

    public static CompositorScene BuildAt(
        IReadOnlyList<RenderVisualSegment> visualSegments,
        int outputWidth,
        int outputHeight,
        int frameRate,
        double timeSeconds)
    {
        var layers = new List<CompositorLayer>();
        var fps = Math.Max(1, frameRate);
        var inv = CultureInfo.InvariantCulture;

        foreach (var seg in visualSegments ?? Enumerable.Empty<RenderVisualSegment>())
        {
            if (seg == null || seg.EndTime <= seg.StartTime)
                continue;

            // Active at this time? [start, end)
            if (timeSeconds < seg.StartTime - Epsilon || timeSeconds >= seg.EndTime - Epsilon)
                continue;

            var duration = seg.EndTime - seg.StartTime;
            var elapsed = Math.Clamp(timeSeconds - seg.StartTime, 0, duration);

            // Destination rectangle (output pixels). OverlayX/Y may be numeric pixel strings
            // (the common case for images and rasterized text) or FFmpeg expressions; only
            // numeric values are honoured, otherwise the layer fills the frame.
            var destW = seg.ScaleWidth ?? outputWidth;
            var destH = seg.ScaleHeight ?? outputHeight;
            var destX = TryParsePixel(seg.OverlayX, inv, 0);
            var destY = TryParsePixel(seg.OverlayY, inv, 0);
            if (seg.ScaleWidth == null && seg.ScaleHeight == null &&
                seg.OverlayX == null && seg.OverlayY == null)
            {
                destX = 0; destY = 0; destW = outputWidth; destH = outputHeight;
            }

            // Fade in/out opacity (matches the renderer's transition semantics), combined with the
            // segment's own opacity (e.g. semi-transparent logo/image overlays).
            var opacity = ComputeFadeOpacity(seg, timeSeconds, duration) * Math.Clamp(seg.Opacity, 0.0, 1.0);
            if (opacity <= Epsilon)
                continue; // fully transparent — nothing to draw

            // Ken-Burns: still images only (videos render their native frames).
            double sx = 1.0, sy = 1.0, tx = 0.0, ty = 0.0;
            var hasMotion = !seg.IsVideo
                && !string.IsNullOrWhiteSpace(seg.MotionPreset)
                && seg.MotionPreset != MotionPresets.None;
            if (hasMotion)
            {
                var refDuration = seg.MotionReferenceDurationSeconds > Epsilon
                    ? seg.MotionReferenceDurationSeconds
                    : duration;
                var progress = refDuration > Epsilon
                    ? Math.Clamp((seg.MotionReferenceOffsetSeconds + elapsed) / refDuration, 0.0, 1.0)
                    : 0.0;
                (sx, sy, tx, ty) = MotionEngine.ComputeTransform(
                    seg.MotionPreset, seg.MotionIntensity, progress, destW, destH, refDuration, fps);
            }

            layers.Add(new CompositorLayer
            {
                SourceKey = seg.SourcePath,
                Kind = seg.IsVideo ? CompositorLayerKind.Video : CompositorLayerKind.Image,
                ZOrder = seg.ZOrder,
                DestX = (float)destX,
                DestY = (float)destY,
                DestWidth = (float)destW,
                DestHeight = (float)destH,
                ScaleMode = ParseScaleMode(seg.ScaleMode),
                Opacity = opacity,
                MotionScaleX = sx,
                MotionScaleY = sy,
                MotionTranslateX = tx,
                MotionTranslateY = ty,
                TintColorHex = string.IsNullOrWhiteSpace(seg.OverlayColorHex) ? null : seg.OverlayColorHex,
                TintOpacity = Math.Clamp(seg.OverlayOpacity, 0.0, 1.0),
                HasAlpha = seg.HasAlpha,
                ClipStartTime = seg.StartTime,
                SourceOffsetSeconds = seg.SourceOffsetSeconds,
            });
        }

        var ordered = layers
            .OrderBy(l => l.ZOrder)
            .ToList();

        return new CompositorScene
        {
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            TimeSeconds = timeSeconds,
            Layers = ordered,
        };
    }

    /// <summary>
    /// Fade-in/out factor in [0,1]. Mirrors the renderer rule: a "fade" transition of duration
    /// d (capped at half the segment) ramps alpha 0→1 over the first d seconds and 1→0 over the last d.
    /// </summary>
    internal static double ComputeFadeOpacity(RenderVisualSegment seg, double timeSeconds, double duration)
    {
        var isFade = string.Equals(seg.TransitionType, "fade", StringComparison.OrdinalIgnoreCase)
            && seg.TransitionDuration > 0
            && seg.TransitionDuration <= duration / 2.0 + Epsilon;
        if (!isFade)
            return 1.0;

        var td = seg.TransitionDuration;
        var elapsed = timeSeconds - seg.StartTime;
        var remaining = seg.EndTime - timeSeconds;

        var fadeIn = elapsed < td ? elapsed / td : 1.0;
        var fadeOut = remaining < td ? remaining / td : 1.0;
        return Math.Clamp(Math.Min(fadeIn, fadeOut), 0.0, 1.0);
    }

    private static double TryParsePixel(string? expr, CultureInfo inv, double fallback)
        => !string.IsNullOrWhiteSpace(expr) && double.TryParse(expr, NumberStyles.Float, inv, out var v)
            ? v
            : fallback;

    private static CompositorScaleMode ParseScaleMode(string? mode) => mode switch
    {
        "Fit" => CompositorScaleMode.Fit,
        "Stretch" => CompositorScaleMode.Stretch,
        _ => CompositorScaleMode.Fill,
    };
}
