using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.Helpers;

internal static class BitmapCacheMetrics
{
    public static long EstimateBitmapBytes(BitmapSource bitmap)
    {
        if (bitmap == null)
            return 1;

        var bitsPerPixel = bitmap.Format.BitsPerPixel;
        if (bitsPerPixel <= 0)
            bitsPerPixel = 32;

        var stride = ((bitmap.PixelWidth * bitsPerPixel) + 7) / 8;
        var bytes = (long)stride * Math.Max(1, bitmap.PixelHeight);
        return Math.Max(1, bytes);
    }
}