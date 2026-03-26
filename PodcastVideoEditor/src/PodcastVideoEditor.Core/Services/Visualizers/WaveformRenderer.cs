using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Oscilloscope-style waveform with smooth cubic interpolation and glow.
/// </summary>
public sealed class WaveformRenderer : IVisualizerRenderer
{
    public string Name => "Waveform";
    public string Description => "Smooth oscilloscope-style waveform display";
    public VisualizerStyle Style => VisualizerStyle.Waveform;

    private readonly SKPaint _wavePaint = new()
    {
        IsAntialias = true,
        StrokeWidth = 2.5f,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round
    };
    private readonly SKPaint _glowPaint = new()
    {
        IsAntialias = true,
        StrokeWidth = 6f,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round
    };
    private readonly SKPath _path = new();

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var centerY = height / 2f;
        var bandCount = Math.Min(config.BandCount, spectrum.Length);
        var pointsPerBand = width / (float)bandCount;
        var maxAmplitude = height * 0.42f;

        var color = VisualizerColorHelper.GetNeonColor(0, bandCount, colorTick, config.ColorPalette, config.PrimaryColorHex);

        // Build smooth cubic path
        _path.Reset();
        _path.MoveTo(0, centerY);

        for (int i = 0; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var x = i * pointsPerBand;
            var y = centerY - (value - 0.5f) * maxAmplitude * 2f;

            if (i == 0)
            {
                _path.LineTo(x, y);
            }
            else
            {
                // Cubic Bezier for smooth curves
                var prevX = (i - 1) * pointsPerBand;
                var cpX = (prevX + x) / 2f;
                _path.CubicTo(cpX, _path.LastPoint.Y, cpX, y, x, y);
            }
        }
        _path.LineTo(width, centerY);

        // Draw glow layer (wider, transparent)
        _glowPaint.Color = color.WithAlpha(60);
        _glowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f);
        canvas.DrawPath(_path, _glowPaint);

        // Draw main line
        _wavePaint.Color = color;
        canvas.DrawPath(_path, _wavePaint);
    }

    public void Dispose()
    {
        _wavePaint.Dispose();
        _glowPaint.Dispose();
        _path.Dispose();
    }
}
