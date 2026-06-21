#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.Compositing;
using PodcastVideoEditor.Ui.Helpers;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Supplies decoded <see cref="SKImage"/> textures to the compositor. Images decode once and
/// are cached; when drawn onto a GPU canvas Skia uploads + caches the texture internally, so a
/// single cached image per source yields a fully GPU-accelerated composite.
///
/// Video layers reuse the existing FFmpeg thumbnail cache: a representative frame near the scene
/// time is loaded (async, throttled) so playback never blocks. Frame-accurate GPU video decode
/// is the next phase; this keeps the foundation working without an unverified decode subsystem.
/// </summary>
public sealed class SkiaImageTextureCache : ICompositorTextureSource, IDisposable
{
    // Cap decoded texture size. Full-resolution photos (e.g. 6000px) can fail/stall GPU texture
    // upload while a CPU raster surface drew them fine — which is exactly why backgrounds showed
    // on raster but vanished on the GPU surface. Pre-scaling to a bound also matches the
    // "pre-scaled GPU textures" approach a real-time compositor uses, so it is faster too.
    private const int MaxTextureDim = 2048;

    private readonly object _lock = new();
    private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKImage> _videoFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingVideo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SKBitmap> _ownedBitmaps = new();
    private readonly Action? _onFrameReady;
    private bool _disposed;

    /// <param name="onFrameReady">Invoked (on a thread-pool thread) when a new video frame is decoded,
    /// so the host can request a repaint. Marshal to the UI thread yourself.</param>
    public SkiaImageTextureCache(Action? onFrameReady = null)
    {
        _onFrameReady = onFrameReady;
    }

    public SKImage? GetImage(CompositorLayer layer, double timeSeconds)
    {
        if (_disposed || layer == null || string.IsNullOrWhiteSpace(layer.SourceKey))
            return null;

        return layer.Kind == CompositorLayerKind.Video
            ? GetVideoFrame(layer.SourceKey, timeSeconds)
            : GetStillImage(layer.SourceKey);
    }

    private SKImage? GetStillImage(string path)
    {
        lock (_lock)
        {
            if (_images.TryGetValue(path, out var cached))
                return cached;
        }

        var image = File.Exists(path) ? DecodeStillBounded(path) : null;

        lock (_lock)
        {
            _images[path] = image; // cache nulls too, to avoid re-decoding broken files every frame
        }
        return image;
    }

    /// <summary>
    /// Decodes a still image, downscaling so its longest side is at most <see cref="MaxTextureDim"/>.
    /// The backing bitmap is retained (and disposed with the cache) so the returned image stays valid.
    /// </summary>
    private SKImage? DecodeStillBounded(string path)
    {
        try
        {
            var full = SKBitmap.Decode(path);
            if (full == null)
                return null;

            SKBitmap bmp;
            var longest = Math.Max(full.Width, full.Height);
            if (longest <= MaxTextureDim)
            {
                bmp = full;
            }
            else
            {
                var s = (double)MaxTextureDim / longest;
                var info = new SKImageInfo(
                    Math.Max(1, (int)(full.Width * s)),
                    Math.Max(1, (int)(full.Height * s)),
                    SKColorType.Rgba8888, SKAlphaType.Premul);
                var scaled = new SKBitmap(info);
                if (full.ScalePixels(scaled, SKFilterQuality.Medium))
                {
                    full.Dispose();
                    bmp = scaled;
                }
                else
                {
                    scaled.Dispose();
                    bmp = full;
                }
            }

            lock (_lock) { _ownedBitmaps.Add(bmp); }
            Log.Debug("Compositor texture decoded: {File} -> {W}x{H}", Path.GetFileName(path), bmp.Width, bmp.Height);
            return SKImage.FromBitmap(bmp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Compositor texture decode FAILED: {Path}", path);
            return null;
        }
    }

    private SKImage? GetVideoFrame(string path, double timeSeconds)
    {
        // Quantize to 0.5s buckets so we don't thrash one extraction per displayed frame.
        var bucket = Math.Round(Math.Max(0, timeSeconds) * 2.0) / 2.0;
        var key = $"{path}@{bucket:F1}";

        lock (_lock)
        {
            if (_videoFrames.TryGetValue(key, out var frame))
                return frame;
        }

        // Fast path: thumbnail already on disk → decode synchronously (cheap, small PNG).
        try
        {
            var cachedPath = FFmpegService.GetThumbnailCachePathFor(path, bucket);
            if (File.Exists(cachedPath))
            {
                var img = DecodeFile(cachedPath);
                if (img != null)
                {
                    lock (_lock) { _videoFrames[key] = img; }
                    return img;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Compositor video frame cache probe failed: {Path}", path);
        }

        // Slow path: generate the thumbnail off the UI thread, throttled globally.
        lock (_lock)
        {
            if (!_pendingVideo.Add(key))
                return GetNearestVideoFrame(path); // already generating; show closest frame meanwhile
        }

        _ = ThumbnailThrottle.RunThrottledAsync(async () =>
        {
            try
            {
                var thumb = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(path, bucket).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(thumb) && File.Exists(thumb))
                {
                    var img = DecodeFile(thumb);
                    if (img != null)
                    {
                        lock (_lock) { _videoFrames[key] = img; }
                        _onFrameReady?.Invoke();
                    }
                }
                return 0;
            }
            finally
            {
                lock (_lock) { _pendingVideo.Remove(key); }
            }
        });

        return GetNearestVideoFrame(path);
    }

    private SKImage? GetNearestVideoFrame(string path)
    {
        lock (_lock)
        {
            foreach (var kvp in _videoFrames)
            {
                if (kvp.Key.StartsWith(path + "@", StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }
        return null;
    }

    private static SKImage? DecodeFile(string path)
    {
        try
        {
            using var data = SKData.Create(path);
            return data != null ? SKImage.FromEncodedData(data) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var img in _images.Values) img?.Dispose();
            foreach (var img in _videoFrames.Values) img.Dispose();
            foreach (var bmp in _ownedBitmaps) bmp.Dispose();
            _images.Clear();
            _videoFrames.Clear();
            _ownedBitmaps.Clear();
            _pendingVideo.Clear();
        }
    }
}
