using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Classic spectrum bars with gradient fill, rounded caps, mirror mode, and peak hold indicators.
/// Enhanced from original DrawBars with bottom-to-top gradient and rounded bar caps.
/// </summary>
public sealed class BarsRenderer : IVisualizerRenderer
{
    public string Name => "Bars";
    public string Description => "Classic spectrum bars with gradient fill and peak indicators";
    public VisualizerStyle Style => VisualizerStyle.Bars;

    private readonly SKPaint _barPaint = new() { IsAntialias = true };
    private readonly SKPaint _peakPaint = new() { IsAntialias = false, Color = SKColors.White };
    private readonly SKPaint _linePaint = new() { IsAntialias = false, Color = new SKColor(255, 255, 255, 40), StrokeWidth = 1f };

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var centerY = height / 2f;
        var bandCount = Math.Min(config.BandCount, spectrum.Length);
        var barWidth = (width - (bandCount - 1) * config.BarSpacing) / bandCount;
        var maxBarHeight = height * 0.45f;
        var cornerRadius = Math.Max(1f, barWidth * 0.2f);

        // Horizon line
        canvas.DrawLine(0, centerY, width, centerY, _linePaint);

        for (int i = 0; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var barHeight = value * maxBarHeight;
            if (barHeight < 1f) barHeight = 1f; // minimum visible bar

            var x = i * (barWidth + config.BarSpacing);
            var color = VisualizerColorHelper.GetNeonColor(i, bandCount, colorTick, config.ColorPalette);

            // Gradient fill: base color at bottom, brighter at top
            var topColor = color.WithAlpha(255);
            var bottomColor = new SKColor(
                (byte)(color.Red * 0.4f), (byte)(color.Green * 0.4f), (byte)(color.Blue * 0.4f), 255);

            // Upper bar (above center)
            var rectTop = new SKRect(x, centerY - barHeight, x + barWidth, centerY);
            _barPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, centerY - barHeight), new SKPoint(x, centerY),
                new[] { topColor, bottomColor }, null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(new SKRoundRect(rectTop, cornerRadius, cornerRadius), _barPaint);

            // Lower bar (below center, mirrored)
            var rectBottom = new SKRect(x, centerY, x + barWidth, centerY + barHeight);
            _barPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, centerY), new SKPoint(x, centerY + barHeight),
                new[] { bottomColor, topColor }, null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(new SKRoundRect(rectBottom, cornerRadius, cornerRadius), _barPaint);

            // Peak caps
            if (config.ShowPeaks && i < peakBars.Length && peakBars[i] > 0.01f)
            {
                var capHeight = 3f;
                var peakHeight = peakBars[i] * maxBarHeight;
                var capYTop = centerY - peakHeight - capHeight - 2f;
                var capYBottom = centerY + peakHeight + 2f;
                canvas.DrawRect(new SKRect(x, capYTop, x + barWidth, capYTop + capHeight), _peakPaint);
                canvas.DrawRect(new SKRect(x, capYBottom, x + barWidth, capYBottom + capHeight), _peakPaint);
            }
        }

        _barPaint.Shader = null;
    }

    public void Dispose()
    {
        _barPaint.Dispose();
        _peakPaint.Dispose();
        _linePaint.Dispose();
    }
}
