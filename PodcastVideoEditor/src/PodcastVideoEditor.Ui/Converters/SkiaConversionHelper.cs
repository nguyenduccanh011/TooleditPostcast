using SkiaSharp;
using SkiaSharp.Views.WPF;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.Converters;

/// <summary>
/// Converts SkiaSharp SKBitmap to WPF BitmapSource for Image.Source binding.
/// Uses SkiaSharp.Views.WPF ToWriteableBitmap extension.
/// </summary>
public static class SkiaConversionHelper
{
    /// <summary>
    /// Convert SKBitmap to WPF WriteableBitmap (BitmapSource).
    /// Returns null if input is null.
    /// </summary>
    public static WriteableBitmap? ToBitmapSource(SKBitmap? bitmap)
    {
        if (bitmap == null)
            return null;

        return bitmap.ToWriteableBitmap();
    }
}
