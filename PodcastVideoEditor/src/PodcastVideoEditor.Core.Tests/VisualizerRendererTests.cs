using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Visualizers;
using SkiaSharp;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

/// <summary>
/// Tests that each IVisualizerRenderer produces non-black output and doesn't crash.
/// </summary>
public class VisualizerRendererTests : IDisposable
{
    private readonly VisualizerRendererRegistry _registry = new();
    private readonly VisualizerConfig _config = new() { BandCount = 32, Style = VisualizerStyle.Bars };
    private readonly float[] _spectrum;
    private readonly float[] _peaks;

    public VisualizerRendererTests()
    {
        // Simulate a typical mid-energy spectrum
        _spectrum = new float[32];
        _peaks = new float[32];
        for (int i = 0; i < 32; i++)
        {
            _spectrum[i] = 0.3f + 0.5f * MathF.Sin(i * MathF.PI / 16f);
            _peaks[i] = _spectrum[i] + 0.1f;
        }
    }

    [Theory]
    [InlineData(VisualizerStyle.Bars)]
    [InlineData(VisualizerStyle.Waveform)]
    [InlineData(VisualizerStyle.Circular)]
    [InlineData(VisualizerStyle.NeonGlow)]
    [InlineData(VisualizerStyle.Particles)]
    [InlineData(VisualizerStyle.Ring)]
    [InlineData(VisualizerStyle.LineWave)]
    public void Renderer_ProducesNonBlackOutput(VisualizerStyle style)
    {
        var renderer = _registry.GetRenderer(style);

        using var bitmap = new SKBitmap(320, 240, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        renderer.Render(canvas, _spectrum, _peaks, _config, 320, 240, 0.5);
        canvas.Flush();

        // At least one pixel should be non-black
        bool hasColor = false;
        for (int y = 0; y < bitmap.Height && !hasColor; y++)
        {
            for (int x = 0; x < bitmap.Width && !hasColor; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                    hasColor = true;
            }
        }

        Assert.True(hasColor, $"{style} renderer produced an all-black frame");
    }

    [Theory]
    [InlineData(VisualizerStyle.Bars)]
    [InlineData(VisualizerStyle.Waveform)]
    [InlineData(VisualizerStyle.Circular)]
    [InlineData(VisualizerStyle.NeonGlow)]
    [InlineData(VisualizerStyle.Particles)]
    [InlineData(VisualizerStyle.Ring)]
    [InlineData(VisualizerStyle.LineWave)]
    public void Renderer_DoesNotCrashWithEmptySpectrum(VisualizerStyle style)
    {
        var renderer = _registry.GetRenderer(style);
        var empty = new float[32];

        using var bitmap = new SKBitmap(320, 240, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        // Should not throw
        renderer.Render(canvas, empty, empty, _config, 320, 240, 0.0);
    }

    [Fact]
    public void Registry_ReturnsAllSevenRenderers()
    {
        Assert.Equal(7, _registry.GetAll().Count);
    }

    [Fact]
    public void Registry_FallsBackToBarsForUnknown()
    {
        // Cast an invalid int to VisualizerStyle
        var renderer = _registry.GetRenderer((VisualizerStyle)999);
        Assert.Equal(VisualizerStyle.Bars, renderer.Style);
    }

    public void Dispose() => _registry.Dispose();
}
