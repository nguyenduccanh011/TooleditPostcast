using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Ring/radial visualization with smooth Bezier curves and gradient fill.
/// Improved version of Circular — connects points with curves, adds fill and rotation.
/// </summary>
public sealed class RingRenderer : IVisualizerRenderer
{
    public string Name => "Ring";
    public string Description => "Smooth radial ring with Bezier curves and gradient fill";
    public VisualizerStyle Style => VisualizerStyle.Ring;

    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
    private readonly SKPaint _glowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5f };
    private readonly SKPath _path = new();

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var centerX = width / 2f;
        var centerY = height / 2f;
        var minDim = Math.Min(width, height);
        var baseRadius = minDim * 0.18f;
        var maxRadiusExtent = minDim * 0.32f;
        var bandCount = Math.Min(config.BandCount, spectrum.Length);
        var rotationAngle = (float)(colorTick * 0.015);

        // Compute ring points
        var points = new SKPoint[bandCount];
        for (int i = 0; i < bandCount; i++)
        {
            var angle = (float)(i * 2 * Math.PI / bandCount) + rotationAngle;
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var radius = baseRadius + value * maxRadiusExtent;
            points[i] = new SKPoint(
                centerX + radius * MathF.Cos(angle),
                centerY + radius * MathF.Sin(angle));
        }

        // Build smooth closed path using cubic Bezier
        _path.Reset();
        if (bandCount < 3) return;

        _path.MoveTo(points[0]);
        for (int i = 0; i < bandCount; i++)
        {
            var curr = points[i];
            var next = points[(i + 1) % bandCount];
            var nextNext = points[(i + 2) % bandCount];

            var cp1 = new SKPoint(
                curr.X + (next.X - points[(i - 1 + bandCount) % bandCount].X) * 0.2f,
                curr.Y + (next.Y - points[(i - 1 + bandCount) % bandCount].Y) * 0.2f);
            var cp2 = new SKPoint(
                next.X - (nextNext.X - curr.X) * 0.2f,
                next.Y - (nextNext.Y - curr.Y) * 0.2f);

            _path.CubicTo(cp1, cp2, next);
        }
        _path.Close();

        // Fill with radial gradient
        var color1 = VisualizerColorHelper.GetNeonColor(0, bandCount, colorTick, config.ColorPalette);
        var color2 = VisualizerColorHelper.GetNeonColor(bandCount / 2, bandCount, colorTick, config.ColorPalette);

        _fillPaint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(centerX, centerY), baseRadius + maxRadiusExtent,
            new[] { color1.WithAlpha(60), color2.WithAlpha(15), SKColors.Transparent },
            new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp);
        canvas.DrawPath(_path, _fillPaint);
        _fillPaint.Shader = null;

        // Glow stroke
        _glowPaint.Color = color1.WithAlpha(50);
        _glowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f);
        canvas.DrawPath(_path, _glowPaint);
        _glowPaint.MaskFilter = null;

        // Main stroke
        _strokePaint.Color = color1;
        canvas.DrawPath(_path, _strokePaint);

        // Inner circle
        _strokePaint.Color = color2.WithAlpha(80);
        canvas.DrawCircle(centerX, centerY, baseRadius * 0.5f, _strokePaint);
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _glowPaint.Dispose();
        _path.Dispose();
    }
}
