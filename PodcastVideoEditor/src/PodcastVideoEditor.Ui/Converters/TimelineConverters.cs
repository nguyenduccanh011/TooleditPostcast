using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Helpers;
using Serilog;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Converts time value (seconds) to pixel position on timeline.
    /// This converter requires ConverterParameter to contain PixelsPerSecond value.
    /// Usage: Value = TimeSeconds, ConverterParameter = PixelsPerSecond
    /// </summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class TimeToPixelsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double timeSeconds) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                return timeSeconds * pixelsPerSecond;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double pixels) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                if (pixelsPerSecond > 0)
                    return pixels / pixelsPerSecond;
            }
            return 0.0;
        }
    }

    /// <summary>
    /// Converts segment duration (EndTime - StartTime, in seconds) to pixel width.
    /// Usage: Binding="{Binding EndTime - StartTime}" with ConverterParameter=PixelsPerSecond
    /// For simplicity, we use a composite approach in code-behind instead.
    /// </summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class DurationToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double durationSeconds) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                return Math.Max(30, durationSeconds * pixelsPerSecond); // Minimum 30px width
            }
            return 100.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convert segment duration display (takes two parameters: EndTime, StartTime).
    /// </summary>
    [ValueConversion(typeof(object), typeof(string))]
    public class DurationDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                double.TryParse(values[0]?.ToString(), out double endTime) &&
                double.TryParse(values[1]?.ToString(), out double startTime))
            {
                double durationSeconds = endTime - startTime;
                string sign = durationSeconds < 0 ? "-" : "";
                durationSeconds = Math.Abs(durationSeconds);
                
                int minutes = (int)(durationSeconds / 60);
                double seconds = durationSeconds % 60;
                
                return $"{sign}{minutes}:{seconds:F2}";
            }
            return "0:00.00";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a double to formatted string with specified format in converter parameter.
    /// Usage: Text="{Binding Value, Converter={StaticResource DoubleFormatConverter}, ConverterParameter='F1'}"
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class DoubleFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double dblValue))
            {
                string format = parameter?.ToString() ?? "F2";
                return dblValue.ToString(format, culture);
            }
            return "0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts PixelsPerSecond value to display format "X.Xpx/s".
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class PixelsPerSecondConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double pxPerSec))
            {
                return $"{pxPerSec:F1}px/s";
            }
            return "0.0px/s";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts time value (in seconds) to display format with 2 decimal places (matches script format [start → end]).
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class TimeValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double timeSeconds))
            {
                return $"{timeSeconds:F2}";
            }
            return "0.00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts transition duration (in seconds) to display format with 3 decimal places.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class TransitionDurationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double durationSeconds))
            {
                return $"{durationSeconds:F3}";
            }
            return "0.000";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when segment is visual and has no background asset (for "No image" placeholder in timeline).
    /// Values: [0]=Kind (string), [1]=BackgroundAssetId (string or null).
    /// </summary>
    public class SegmentNoImagePlaceholderVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return Visibility.Collapsed;
            var kind = values[0]?.ToString() ?? "";
            var assetId = values[1]?.ToString();
            if (string.Equals(kind, "visual", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(assetId))
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts (BackgroundAssetId, Assets collection) to ImageSource for segment thumbnail (CapCut-style preview).
    /// Values: [0]=BackgroundAssetId (string), [1]=Assets (IEnumerable or ICollection of Asset).
    /// Returns BitmapImage for Image assets, null otherwise. Uses LRU cache for performance.
    /// </summary>
    public class SegmentThumbnailSourceConverter : IMultiValueConverter
    {
        private static readonly LRUCache<string, BitmapImage> s_cache = new(1000); // Increased from 500
        private const int ThumbnailDecodePixelWidth = 60; // Reduced from 120 for faster loading
        // Track pending async generation
        private static readonly HashSet<string> s_pendingKeys = new();
        private static readonly object s_pendingLock = new();

        internal static Uri? CreateFileUriStatic(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                return null;
            try
            {
                if (!Path.IsPathRooted(fullPath))
                    fullPath = Path.GetFullPath(fullPath);
                var uriStr = "file:///" + fullPath.Replace('\\', '/').TrimStart('/');
                return new Uri(uriStr, UriKind.Absolute);
            }
            catch
            {
                return null;
            }
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return null!;
            var assetId = values[0]?.ToString();
            if (string.IsNullOrWhiteSpace(assetId))
                return null!;
            if (values[1] is not IEnumerable assetsEnum)
                return null!;
            Asset? asset = null;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, assetId, StringComparison.Ordinal))
                {
                    asset = a;
                    break;
                }
            }
            if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath))
                return null!;
            string? imagePath = null;
            string cacheKey = asset.FilePath;
            if (string.Equals(asset.Type, "Image", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(asset.FilePath))
                    return null!;
                imagePath = Path.GetFullPath(asset.FilePath);
            }
            else if (string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
            {
                cacheKey = asset.FilePath + "_v0";
                
                // Check LRU cache first (fast path)
                if (s_cache.TryGet(cacheKey, out var videoCache))
                    return videoCache;
                
                // Check if thumbnail file already exists (disk cache)
                var existingPath = FFmpegService.GetThumbnailCachePathFor(asset.FilePath, 0);
                if (File.Exists(existingPath))
                {
                    imagePath = Path.GetFullPath(existingPath);
                }
                else
                {
                    // ASYNC fire-and-forget: generate thumbnail without blocking UI
                    lock (s_pendingLock)
                    {
                        if (!s_pendingKeys.Contains(cacheKey))
                        {
                            s_pendingKeys.Add(cacheKey);
                            var capturedPath = asset.FilePath;
                            var capturedKey = cacheKey;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var thumbPath = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(capturedPath, 0);
                                    if (string.IsNullOrEmpty(thumbPath) || !File.Exists(thumbPath))
                                        return;
                                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        try
                                        {
                                            var uri = CreateFileUriStatic(Path.GetFullPath(thumbPath));
                                            if (uri == null) return;
                                            var bmp = new BitmapImage();
                                            bmp.BeginInit();
                                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                                            bmp.UriSource = uri;
                                            bmp.DecodePixelWidth = ThumbnailDecodePixelWidth;
                                            bmp.EndInit();
                                            bmp.Freeze();
                                            s_cache.Add(capturedKey, bmp);
                                        }
                                        catch { }
                                    });
                                }
                                finally
                                {
                                    lock (s_pendingLock) { s_pendingKeys.Remove(capturedKey); }
                                }
                            });
                        }
                    }
                    return null!; // Placeholder until async generation completes
                }
            }
            else
                return null!;
            
            // Check LRU cache before loading
            if (s_cache.TryGet(cacheKey, out var cached))
                return cached;
            
            try
            {
                var uri = CreateFileUriStatic(imagePath);
                if (uri == null)
                    return null!;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = ThumbnailDecodePixelWidth;
                bmp.EndInit();
                bmp.Freeze();
                
                // Add to LRU cache (auto-evicts if full)
                s_cache.Add(cacheKey, bmp);
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to load thumbnail from {Path}", imagePath);
                return null!;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns "Video" when segment has BackgroundAssetId and asset type is Video but no thumbnail yet (e.g. FFmpeg not ready).
    /// </summary>
    public class SegmentVideoPlaceholderVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return Visibility.Collapsed;
            var assetId = values[0]?.ToString();
            if (string.IsNullOrWhiteSpace(assetId))
                return Visibility.Collapsed;
            if (values[1] is not IEnumerable assetsEnum)
                return Visibility.Collapsed;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, assetId, StringComparison.Ordinal))
                    return string.Equals(a.Type, "Video", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when no asset or asset is Video (single thumb = frame đầu tiên); Collapsed when asset is Image (dùng tiled brush). Luôn hiển thị ảnh đơn cho Video làm fallback khi strip chưa load. Values: [0]=BackgroundAssetId, [1]=Assets.
    /// </summary>
    public class AssetIdToSingleThumbVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return Visibility.Visible;
            var assetId = values[0]?.ToString();
            if (string.IsNullOrWhiteSpace(assetId))
                return Visibility.Visible;
            if (values[1] is not IEnumerable assetsEnum)
                return Visibility.Visible;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, assetId, StringComparison.Ordinal))
                    return string.Equals(a.Type, "Image", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns ImageBrush with TileMode.Tile for Image assets so image repeats when segment is long (CapCut-style).
    /// Values: [0]=BackgroundAssetId, [1]=Assets.
    /// </summary>
    public class SegmentThumbnailBrushConverter : IMultiValueConverter
    {
        private static readonly Dictionary<string, ImageBrush> s_brushCache = new();
        private static readonly SolidColorBrush s_defaultBrush = new(Color.FromRgb(0x1a, 0x1a, 0x2e));
        private const int TileSize = 80;

        static SegmentThumbnailBrushConverter()
        {
            s_defaultBrush.Freeze();
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return s_defaultBrush;
            var assetId = values[0]?.ToString();
            if (string.IsNullOrWhiteSpace(assetId))
                return null!;
            if (values[1] is not IEnumerable assetsEnum)
                return null!;
            Asset? asset = null;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, assetId, StringComparison.Ordinal))
                {
                    asset = a;
                    break;
                }
            }
            if (asset == null || !string.Equals(asset.Type, "Image", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
                return s_defaultBrush;
            lock (s_brushCache)
            {
                if (s_brushCache.TryGetValue(asset.FilePath, out var cached))
                    return cached;
            }
            try
            {
                var fullPath = Path.GetFullPath(asset.FilePath);
                var uri = SegmentThumbnailSourceConverter.CreateFileUriStatic(fullPath);
                if (uri == null)
                    return s_defaultBrush;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = TileSize;
                bmp.EndInit();
                bmp.Freeze();
                var brush = new ImageBrush(bmp)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, TileSize, TileSize),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None
                };
                brush.Freeze();
                lock (s_brushCache)
                {
                    if (s_brushCache.Count < 200)
                        s_brushCache[asset.FilePath] = brush;
                }
                return brush;
            }
            catch
            {
                return s_defaultBrush;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns list of time offsets (0, d*0.25, d*0.5, d*0.75, d) for video segment strip thumbnails.
    /// Values: [0]=Segment, [1]=Assets, [2]=EndTime, [3]=IsDeferringThumbnailUpdate. When deferring (resize/drag), returns empty to avoid lag. Returns IEnumerable of double (empty if not video).
    /// </summary>
    public class SegmentStripTimesConverter : IMultiValueConverter
    {
        private static readonly double[] s_empty = Array.Empty<double>();
        private static readonly Dictionary<string, double[]> s_lastTimesBySegmentId = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || values[0] is not Segment seg || values[1] is not IEnumerable assetsEnum)
                return s_empty;

            // Keep showing previously computed strip times during drag/resize to avoid thumbnail flicker.
            if (values.Length >= 4 && values[3] is true)
            {
                if (!string.IsNullOrWhiteSpace(seg.Id) && s_lastTimesBySegmentId.TryGetValue(seg.Id, out var cached))
                    return cached;
                return s_empty;
            }
            if (string.IsNullOrWhiteSpace(seg.BackgroundAssetId))
                return s_empty;
            Asset? asset = null;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, seg.BackgroundAssetId, StringComparison.Ordinal))
                {
                    asset = a;
                    break;
                }
            }
            if (asset == null || !string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                return s_empty;

            var d = seg.EndTime - seg.StartTime;
            double[] result;
            if (d <= 0)
                result = new[] { 0.0 };
            else
                result = new[] { 0.0, d * 0.5, d };  // 3 frames: start, middle, end

            if (!string.IsNullOrWhiteSpace(seg.Id))
            {
                lock (s_lastTimesBySegmentId)
                {
                    s_lastTimesBySegmentId[seg.Id] = result;
                    if (s_lastTimesBySegmentId.Count > 500)
                        s_lastTimesBySegmentId.Clear();
                }
            }

            return result;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns BitmapImage for video frame at segment.StartTime + timeOffset. Values: [0]=timeOffset (double), [1]=Segment, [2]=Assets.
    /// Uses LRU cache for efficient memory management. Generates thumbnails async on cache miss (fire-and-forget).
    /// </summary>
    public class VideoFrameAtTimeConverter : IMultiValueConverter
    {
        private static readonly LRUCache<string, BitmapImage> s_frameCache = new(1000); // Increased cache
        private const int DecodeWidth = 40; // Smaller = faster decode
        private static DateTime _lastGenerateTime = DateTime.MinValue;
        private const int ThrottleMs = 16; // ~60fps max generation rate
        // Track pending async generation to avoid duplicate work
        private static readonly HashSet<string> s_pendingKeys = new();
        private static readonly object s_pendingLock = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3)
                return null!;
            if (!double.TryParse(values[0]?.ToString(), out double timeOffset) || values[1] is not Segment seg || values[2] is not IEnumerable assetsEnum)
                return null!;

            var isDeferring = values.Length >= 4 && values[3] is true;
            if (string.IsNullOrWhiteSpace(seg.BackgroundAssetId))
                return null!;
            Asset? asset = null;
            foreach (var item in assetsEnum)
            {
                if (item is Asset a && string.Equals(a.Id, seg.BackgroundAssetId, StringComparison.Ordinal))
                {
                    asset = a;
                    break;
                }
            }
            if (asset == null || !string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(asset.FilePath))
                return null!;
            var timeInVideo = seg.StartTime + timeOffset;
            if (timeInVideo < 0)
                timeInVideo = 0;
            var cacheKey = $"{asset.FilePath}|{timeInVideo:F2}";
            
            // Check LRU cache first
            if (s_frameCache.TryGet(cacheKey, out var cachedFrame))
                return cachedFrame;
            
            // Throttle generation during rapid scrolling
            if (isDeferring)
                return null!; // Skip during drag/resize
            
            var now = DateTime.UtcNow;
            if ((now - _lastGenerateTime).TotalMilliseconds < ThrottleMs)
                return null!; // Skip if generating too fast
            
            _lastGenerateTime = now;
            
            // Check if already being generated async
            lock (s_pendingLock)
            {
                if (s_pendingKeys.Contains(cacheKey))
                    return null!; // Already in progress
            }

            // ASYNC fire-and-forget: generate thumbnail in background, then update UI
            lock (s_pendingLock) { s_pendingKeys.Add(cacheKey); }
            
            var capturedAssetPath = asset.FilePath;
            _ = Task.Run(async () =>
            {
                try
                {
                    var thumbPath = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(capturedAssetPath, timeInVideo);
                    if (string.IsNullOrEmpty(thumbPath) || !File.Exists(thumbPath))
                        return;
                    
                    // Load bitmap on UI thread and add to cache
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fullPath = Path.GetFullPath(thumbPath!);
                            var uri = SegmentThumbnailSourceConverter.CreateFileUriStatic(fullPath);
                            if (uri == null) return;
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = uri;
                            bmp.DecodePixelWidth = DecodeWidth;
                            bmp.EndInit();
                            bmp.Freeze();
                            s_frameCache.Add(cacheKey, bmp);
                        }
                        catch { /* ignore load errors */ }
                    });
                }
                finally
                {
                    lock (s_pendingLock) { s_pendingKeys.Remove(cacheKey); }
                }
            });
            
            return null!; // Return null immediately, cached value used on next binding evaluation
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
