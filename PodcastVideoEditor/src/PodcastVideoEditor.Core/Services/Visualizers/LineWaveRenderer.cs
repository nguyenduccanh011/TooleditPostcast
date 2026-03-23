using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Modern minimal line wave — multiple layered smooth waves with different opacity.
/// Cubic Bezier interpolation for extremely smooth curves.
/// </summary>
public sealed class LineWaveRenderer : IVisualizerRenderer
{
    public string Name => "Line Wave";
    public string Description => "Modern minimal smooth wave lines with layered opacity";
    public VisualizerStyle Style => VisualizerStyle.LineWave;

    private readonly SKPaint _wavePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };
    private readonly SKPaint _fillPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };
    private readonly SKPath _path = new();

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var bandCount = Math.Min(config.BandCount, spectrum.Length);
        if (bandCount < 2) return;

        // Draw 3 layered waves with different offsets and opacity
        DrawWaveLayer(canvas, spectrum, config, width, height, colorTick, bandCount,
                      amplitudeScale: 1.0f, alpha: 200, thickness: 2.5f, phaseOffset: 0f);
        DrawWaveLayer(canvas, spectrum, config, width, height, colorTick, bandCount,
                      amplitudeScale: 0.7f, alpha: 100, thickness: 1.8f, phaseOffset: 0.3f);
        DrawWaveLayer(canvas, spectrum, config, width, height, colorTick, bandCount,
                      amplitudeScale: 0.4f, alpha: 50, thickness: 1.2f, phaseOffset: 0.6f);
    }

    private void DrawWaveLayer(SKCanvas canvas, float[] spectrum, VisualizerConfig config,
                               int width, int height, double colorTick, int bandCount,
                               float amplitudeScale, byte alpha, float thickness, float phaseOffset)
    {
        var centerY = height / 2f;
        var maxAmplitude = height * 0.40f * amplitudeScale;
        var pointSpacing = width / (float)(bandCount - 1);

        var color = VisualizerColorHelper.GetNeonColor(
            (int)(phaseOffset * bandCount), bandCount, colorTick, config.ColorPalette);

        // Build smooth cubic path
        _path.Reset();
        var firstY = centerY - (Math.Clamp(spectrum[0], 0f, 1f) - 0.5f) * maxAmplitude * 2f;
        _path.MoveTo(0, firstY);

        for (int i = 1; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var x = i * pointSpacing;
            var y = centerY - (value - 0.5f) * maxAmplitude * 2f;

            var prevX = (i - 1) * pointSpacing;
            var cpX = (prevX + x) / 2f;
            _path.CubicTo(cpX, _path.LastPoint.Y, cpX, y, x, y);
        }

        // Fill area under curve
        {
            var fillPath = new SKPath(_path);
            fillPath.LineTo(width, height);
            fillPath.LineTo(0, height);
            fillPath.Close();

            _fillPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, centerY - maxAmplitude),
                new SKPoint(0, height),
                new[] { color.WithAlpha((byte)(alpha / 3)), SKColors.Transparent },
                null, SKShaderTileMode.Clamp);
            canvas.DrawPath(fillPath, _fillPaint);
            _fillPaint.Shader = null;
            fillPath.Dispose();
        }

        // Stroke
        _wavePaint.StrokeWidth = thickness;
        _wavePaint.Color = color.WithAlpha(alpha);
        canvas.DrawPath(_path, _wavePaint);
    }

    public void Dispose()
    {
        _wavePaint.Dispose();
        _fillPaint.Dispose();
        _path.Dispose();
    }
}
