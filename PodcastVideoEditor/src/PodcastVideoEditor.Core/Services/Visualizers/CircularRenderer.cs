using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Radial/polar visualization with lines from center.
/// </summary>
public sealed class CircularRenderer : IVisualizerRenderer
{
    public string Name => "Circular";
    public string Description => "Radial spectrum with lines radiating from center";
    public VisualizerStyle Style => VisualizerStyle.Circular;

    private readonly SKPaint _paint = new() { IsAntialias = true, StrokeWidth = 2f };

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var centerX = width / 2f;
        var centerY = height / 2f;
        var baseRadius = Math.Min(width, height) * 0.2f;
        var maxRadiusExtent = Math.Min(width, height) * 0.35f;
        var bandCount = Math.Min(config.BandCount, spectrum.Length);

        var gradient = VisualizerColorHelper.GetColorGradient(config.ColorPalette);

        for (int i = 0; i < bandCount; i++)
        {
            var angle = (float)(i * 2 * Math.PI / bandCount) + (float)(colorTick * 0.01);
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var radius = baseRadius + value * maxRadiusExtent;

            var x = centerX + radius * MathF.Cos(angle);
            var y = centerY + radius * MathF.Sin(angle);

            _paint.Color = gradient[i % gradient.Length];
            canvas.DrawLine(centerX, centerY, x, y, _paint);
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
    }
}
