using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Neon glow bars — CapCut-inspired. Renders bars twice: blurred glow layer + crisp solid layer.
/// Creates a vibrant, club-style neon look.
/// </summary>
public sealed class NeonGlowRenderer : IVisualizerRenderer
{
    public string Name => "Neon Glow";
    public string Description => "Vibrant neon bars with outer glow effect";
    public VisualizerStyle Style => VisualizerStyle.NeonGlow;

    private readonly SKPaint _glowPaint = new() { IsAntialias = true };
    private readonly SKPaint _barPaint = new() { IsAntialias = true };
    private readonly SKPaint _linePaint = new()
    {
        IsAntialias = false,
        Color = new SKColor(255, 255, 255, 25),
        StrokeWidth = 1f
    };

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var centerY = height / 2f;
        var bandCount = Math.Min(config.BandCount, spectrum.Length);
        var barWidth = (width - (bandCount - 1) * config.BarSpacing) / bandCount;
        var maxBarHeight = height * 0.44f;
        var cornerRadius = Math.Max(1f, barWidth * 0.25f);

        // Dim horizon line
        canvas.DrawLine(0, centerY, width, centerY, _linePaint);

        // Pass 1: Glow layer (wide blur behind bars)
        _glowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(3f, barWidth * 0.5f));
        for (int i = 0; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var barHeight = Math.Max(2f, value * maxBarHeight);
            var x = i * (barWidth + config.BarSpacing);
            var color = VisualizerColorHelper.GetNeonColor(i, bandCount, colorTick, config.ColorPalette, config.PrimaryColorHex);

            _glowPaint.Color = color.WithAlpha((byte)(100 * value + 30));

            // Glow rect slightly larger than bar
            var expand = barWidth * 0.3f;
            var rectTop = new SKRect(x - expand, centerY - barHeight - expand, x + barWidth + expand, centerY + expand);
            canvas.DrawRoundRect(new SKRoundRect(rectTop, cornerRadius * 2, cornerRadius * 2), _glowPaint);

            var rectBot = new SKRect(x - expand, centerY - expand, x + barWidth + expand, centerY + barHeight + expand);
            canvas.DrawRoundRect(new SKRoundRect(rectBot, cornerRadius * 2, cornerRadius * 2), _glowPaint);
        }
        _glowPaint.MaskFilter = null;

        // Pass 2: Solid crisp bars on top
        for (int i = 0; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            var barHeight = Math.Max(2f, value * maxBarHeight);
            var x = i * (barWidth + config.BarSpacing);
            var color = VisualizerColorHelper.GetNeonColor(i, bandCount, colorTick, config.ColorPalette, config.PrimaryColorHex);

            _barPaint.Color = color;

            var rectTop = new SKRect(x, centerY - barHeight, x + barWidth, centerY);
            canvas.DrawRoundRect(new SKRoundRect(rectTop, cornerRadius, cornerRadius), _barPaint);

            var rectBot = new SKRect(x, centerY, x + barWidth, centerY + barHeight);
            canvas.DrawRoundRect(new SKRoundRect(rectBot, cornerRadius, cornerRadius), _barPaint);
        }
    }

    public void Dispose()
    {
        _glowPaint.Dispose();
        _barPaint.Dispose();
        _linePaint.Dispose();
    }
}
