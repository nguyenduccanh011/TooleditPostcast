using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.Converters;

/// <summary>
/// Converts SkiaSharp SKBitmap to WPF BitmapSource for Image.Source binding.
/// Reuses a single WriteableBitmap to avoid GC pressure from ~30 allocations/second.
/// Thread-safe: all static state guarded by <see cref="_lock"/>.
/// </summary>
public static class SkiaConversionHelper
{
    private static readonly object _lock = new();
    private static WriteableBitmap? _reusableBitmap;
    private static byte[]? _pixelBuffer;

    /// <summary>
    /// Convert SKBitmap to WPF WriteableBitmap, reusing the same bitmap when dimensions match.
    /// Returns null if input is null.
    /// </summary>
    public static WriteableBitmap? ToBitmapSource(SKBitmap? bitmap)
    {
        if (bitmap == null)
            return null;

        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0)
            return null;

        lock (_lock)
        {
            // Reuse existing WriteableBitmap if dimensions match
            if (_reusableBitmap == null
                || _reusableBitmap.PixelWidth != width
                || _reusableBitmap.PixelHeight != height)
            {
                _reusableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            }

            // Fast pixel copy: SKBitmap (RGBA) → WriteableBitmap (PBGRA)
            _reusableBitmap.Lock();
            try
            {
                var srcPtr = bitmap.GetPixels();
                if (srcPtr == IntPtr.Zero)
                    return null;

                var pixelCount = width * height;
                var byteCount = pixelCount * 4;

                // Copy RGBA pixels from native memory to managed array
                if (_pixelBuffer == null || _pixelBuffer.Length < byteCount)
                    _pixelBuffer = new byte[byteCount];

                Marshal.Copy(srcPtr, _pixelBuffer, 0, byteCount);

                // RGBA → BGRA swizzle using Span for better performance
                var span = _pixelBuffer.AsSpan(0, byteCount);
                for (int i = 0; i < span.Length - 3; i += 4)
                {
                    (span[i], span[i + 2]) = (span[i + 2], span[i]); // Swap R ↔ B
                }

                // Copy swizzled BGRA pixels into WriteableBitmap back buffer
                Marshal.Copy(_pixelBuffer, 0, _reusableBitmap.BackBuffer, byteCount);

                _reusableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                _reusableBitmap.Unlock();
            }

            return _reusableBitmap;
        }
    }
}
