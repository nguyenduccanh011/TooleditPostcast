#nullable enable
using System;
using System.Collections.Generic;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Shared "build scene for the current playhead and draw it fitted into the surface" logic used by
/// both the raster and GPU preview controls. Keeping it in one place guarantees the two backends
/// produce identical output and avoids drift.
/// </summary>
internal static class CompositorPresent
{
    public static void Paint(
        SKCanvas canvas,
        float surfaceWidth,
        float surfaceHeight,
        SkiaSceneRenderer renderer,
        IReadOnlyList<RenderVisualSegment>? segments,
        double timeSeconds,
        int outputWidth,
        int outputHeight,
        int frameRate)
    {
        var outW = Math.Max(1, outputWidth);
        var outH = Math.Max(1, outputHeight);

        var scene = CompositorSceneBuilder.BuildAt(
            segments ?? Array.Empty<RenderVisualSegment>(),
            outW, outH, frameRate, timeSeconds);

        // Fit the output frame into the surface (contain + centre); letterbox stays black.
        var scale = Math.Min(surfaceWidth / outW, surfaceHeight / outH);
        if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale))
            scale = 1f;
        var dx = (surfaceWidth - outW * scale) / 2f;
        var dy = (surfaceHeight - outH * scale) / 2f;

        canvas.Clear(SKColors.Black);
        canvas.Save();
        try
        {
            canvas.Translate(dx, dy);
            canvas.Scale(scale);
            renderer.Render(canvas, scene);
        }
        finally
        {
            canvas.Restore();
        }
    }
}
