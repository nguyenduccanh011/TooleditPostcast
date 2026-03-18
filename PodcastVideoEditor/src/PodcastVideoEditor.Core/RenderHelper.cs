#nullable enable
using Serilog;
using SkiaSharp;
using System;
using System.IO;

namespace PodcastVideoEditor.Core;

/// <summary>
/// Minimal helpers for render pipeline (e.g. placeholder image, image layout).
/// </summary>
public static class RenderHelper
{
    /// <summary>
    /// Compute the destination rectangle for an image given a layout preset and frame dimensions.
    /// Returns (X, Y, Width, Height) in pixels, letter-boxed/centered as needed.
    /// </summary>
    public static (double X, double Y, double Width, double Height) ComputeImageRect(
        string preset, double frameW, double frameH) => preset switch
    {
        // 1:1 square, centered vertically
        Models.ImageLayoutPresets.Square_Center =>
            (0, (frameH - frameW) / 2, frameW, frameW),

        // 16:9 widescreen, centered vertically
        Models.ImageLayoutPresets.Widescreen_Center =>
            (0, (frameH - frameW * 9.0 / 16.0) / 2, frameW, frameW * 9.0 / 16.0),

        // FullFrame or unknown — fill entire frame
        _ => (0, 0, frameW, frameH),
    };


    /// <summary>
    /// Create a simple placeholder image file (PNG) for render when project has no image.
    /// </summary>
    public static string CreatePlaceholderImage(string filePath, int width, int height)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(45, 45, 48)); // Dark gray
            using var paint = new SKPaint { Color = new SKColor(120, 120, 120), TextSize = 32 };
            var text = "No image";
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            canvas.DrawText(text, (width - bounds.Width) / 2, height / 2 + bounds.Height / 2, paint);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(filePath);
            data.SaveTo(fs);
            Log.Information("Placeholder image created: {Path}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create placeholder image");
            throw;
        }
    }
}
