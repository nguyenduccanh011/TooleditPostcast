#nullable enable
using System;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// Draws a <see cref="CompositorScene"/> onto a SkiaSharp <see cref="SKCanvas"/> in a single pass.
/// The canvas may be CPU-backed (unit tests, fallback) or GPU-backed (live preview / export):
/// the renderer is identical, so preview and export are guaranteed pixel-consistent.
///
/// One scene = one frame = one composite pass — this is the fundamental difference from the
/// per-render FFmpeg filter graph (no per-frame 'enable' evaluation of inactive overlays).
/// </summary>
public sealed class SkiaSceneRenderer
{
    private readonly ICompositorTextureSource _textures;
    private int _lastDiagSecond = -1;

    public SkiaSceneRenderer(ICompositorTextureSource textures)
    {
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
    }

    /// <summary>Optional source of live visualizer layers, drawn on top of the image layers.</summary>
    public ICompositorVisualizerSource? Visualizers { get; set; }

    public void Render(SKCanvas canvas, CompositorScene scene)
    {
        if (canvas == null) throw new ArgumentNullException(nameof(canvas));
        if (scene == null) throw new ArgumentNullException(nameof(scene));

        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
        };

        int drawn = 0, missingTexture = 0, imageLayers = 0, videoLayers = 0;

        foreach (var layer in scene.Layers)
        {
            if (layer.Kind == CompositorLayerKind.Video) videoLayers++; else imageLayers++;

            var image = _textures.GetImage(layer, scene.TimeSeconds);
            if (image == null)
            {
                missingTexture++;
                continue;
            }
            drawn++;

            var dest = new SKRect(
                layer.DestX,
                layer.DestY,
                layer.DestX + layer.DestWidth,
                layer.DestY + layer.DestHeight);
            if (dest.Width <= 0 || dest.Height <= 0)
                continue;

            var fitRect = ComputeFitRect(image.Width, image.Height, dest, layer.ScaleMode);

            canvas.Save();
            try
            {
                // Clip to the layer's box in output space, then apply the Ken-Burns transform
                // around the box centre so zoom/pan stays contained within the layer bounds.
                canvas.ClipRect(dest, antialias: true);

                var cx = dest.MidX;
                var cy = dest.MidY;
                canvas.Translate(cx, cy);
                canvas.Scale((float)layer.MotionScaleX, (float)layer.MotionScaleY);
                canvas.Translate(-cx, -cy);
                canvas.Translate((float)layer.MotionTranslateX, (float)layer.MotionTranslateY);

                paint.Color = SKColors.White.WithAlpha(ToAlpha(layer.Opacity));
                canvas.DrawImage(image, fitRect, paint);
            }
            finally
            {
                canvas.Restore();
            }

            // Solid colour tint on top (uniform over the box, unaffected by Ken-Burns).
            if (layer.TintOpacity > 0 && !string.IsNullOrWhiteSpace(layer.TintColorHex)
                && SKColor.TryParse(layer.TintColorHex, out var tint))
            {
                using var tintPaint = new SKPaint
                {
                    Color = tint.WithAlpha(ToAlpha(layer.TintOpacity * layer.Opacity)),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true,
                };
                canvas.Save();
                try
                {
                    canvas.ClipRect(dest, antialias: true);
                    canvas.DrawRect(dest, tintPaint);
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }

        // Visualizer layers are drawn procedurally on top of the composited image layers.
        var visualizers = Visualizers;
        if (visualizers != null)
        {
            foreach (var vd in visualizers.GetActiveDraws(scene.TimeSeconds))
            {
                var d = vd.Dest;
                if (d.Width <= 0 || d.Height <= 0 || vd.Painter == null)
                    continue;
                canvas.Save();
                try
                {
                    canvas.ClipRect(d, antialias: true);
                    canvas.Translate(d.Left, d.Top);
                    vd.Painter.Paint(canvas, (int)d.Width, (int)d.Height, scene.TimeSeconds);
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }

        var sec = (int)Math.Floor(scene.TimeSeconds);
        if (sec != _lastDiagSecond)
        {
            _lastDiagSecond = sec;
            Log.Debug(
                "Compositor scene @t={Time:F2}: {Total} layers (image={Img}, video={Vid}); drawn={Drawn}, missingTexture={Missing}",
                scene.TimeSeconds, scene.Layers.Count, imageLayers, videoLayers, drawn, missingTexture);
        }
    }

    /// <summary>
    /// Computes where to draw the source image inside the destination rect for the given scale mode.
    /// Fill = cover (crop), Fit = contain (letterbox), Stretch = exact.
    /// </summary>
    internal static SKRect ComputeFitRect(int imgWidth, int imgHeight, SKRect dest, CompositorScaleMode mode)
    {
        if (imgWidth <= 0 || imgHeight <= 0)
            return dest;

        if (mode == CompositorScaleMode.Stretch)
            return dest;

        var scaleX = dest.Width / imgWidth;
        var scaleY = dest.Height / imgHeight;
        var scale = mode == CompositorScaleMode.Fit
            ? Math.Min(scaleX, scaleY)   // contain
            : Math.Max(scaleX, scaleY);  // cover (Fill)

        var w = imgWidth * scale;
        var h = imgHeight * scale;
        var left = dest.MidX - w / 2f;
        var top = dest.MidY - h / 2f;
        return new SKRect(left, top, left + w, top + h);
    }

    private static byte ToAlpha(double opacity)
        => (byte)Math.Clamp(Math.Round(opacity * 255.0), 0, 255);
}
