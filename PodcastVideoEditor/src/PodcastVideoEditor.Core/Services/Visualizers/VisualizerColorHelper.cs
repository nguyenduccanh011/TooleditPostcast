using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Shared color utilities for all visualizer renderers.
/// Extracted from VisualizerService for reuse across strategy classes.
/// </summary>
public static class VisualizerColorHelper
{
    public static SKColor GetNeonColor(int index, int total, double colorTick, ColorPalette palette)
    {
        var hue = (index * 3.0) + colorTick;
        return palette switch
        {
            ColorPalette.Fire => HslToRgb(hue * 0.8, 1.0, 0.55),
            ColorPalette.Ocean => HslToRgb(200 + hue * 0.3, 0.9, 0.55),
            ColorPalette.Mono => HslToRgb(0, 0, 0.8),
            ColorPalette.Purple => HslToRgb(270 + hue * 0.4, 0.9, 0.6),
            _ => HslToRgb(hue, 1.0, 0.55)
        };
    }

    public static SKColor HslToRgb(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - c / 2;

        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        var r = (byte)Math.Clamp((r1 + m) * 255, 0, 255);
        var g = (byte)Math.Clamp((g1 + m) * 255, 0, 255);
        var b = (byte)Math.Clamp((b1 + m) * 255, 0, 255);
        return new SKColor(r, g, b);
    }

    public static SKColor[] GetColorGradient(ColorPalette palette)
    {
        return palette switch
        {
            ColorPalette.Rainbow => new[]
            {
                SKColor.Parse("#FF0000"),
                SKColor.Parse("#FF7F00"),
                SKColor.Parse("#FFFF00"),
                SKColor.Parse("#00FF00"),
                SKColor.Parse("#0000FF"),
                SKColor.Parse("#4B0082"),
                SKColor.Parse("#9400D3")
            },
            ColorPalette.Fire => new[]
            {
                SKColor.Parse("#000000"),
                SKColor.Parse("#FF0000"),
                SKColor.Parse("#FF7F00"),
                SKColor.Parse("#FFFF00"),
                SKColor.Parse("#FFFFFF")
            },
            ColorPalette.Ocean => new[]
            {
                SKColor.Parse("#000000"),
                SKColor.Parse("#000080"),
                SKColor.Parse("#0000FF"),
                SKColor.Parse("#00FFFF"),
                SKColor.Parse("#FFFFFF")
            },
            ColorPalette.Purple => new[]
            {
                SKColor.Parse("#000000"),
                SKColor.Parse("#4B0082"),
                SKColor.Parse("#800080"),
                SKColor.Parse("#FF00FF"),
                SKColor.Parse("#FFFFFF")
            },
            _ => new[]
            {
                SKColor.Parse("#000000"),
                SKColor.Parse("#808080"),
                SKColor.Parse("#FFFFFF")
            }
        };
    }

    /// <summary>
    /// Get a color from gradient array by interpolation position [0..1].
    /// </summary>
    public static SKColor LerpGradient(SKColor[] gradient, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (gradient.Length == 0) return SKColors.White;
        if (gradient.Length == 1) return gradient[0];

        float pos = t * (gradient.Length - 1);
        int lo = (int)pos;
        int hi = Math.Min(lo + 1, gradient.Length - 1);
        float frac = pos - lo;

        return new SKColor(
            (byte)(gradient[lo].Red + (gradient[hi].Red - gradient[lo].Red) * frac),
            (byte)(gradient[lo].Green + (gradient[hi].Green - gradient[lo].Green) * frac),
            (byte)(gradient[lo].Blue + (gradient[hi].Blue - gradient[lo].Blue) * frac),
            255);
    }
}
