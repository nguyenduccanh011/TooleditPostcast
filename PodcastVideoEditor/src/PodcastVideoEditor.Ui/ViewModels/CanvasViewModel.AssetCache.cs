// CanvasViewModel.AssetCache.cs — Asset lookup dictionary and LRU frame cache.
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class CanvasViewModel
    {
        private Asset? FindAssetById(string? assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                return null;

            var assets = _projectViewModel?.CurrentProject?.Assets;
            if (assets == null)
                return null;

            EnsureAssetDictionaryCurrent(assets);

            if (_assetDictionary == null)
                return null;

            return _assetDictionary.TryGetValue(assetId, out var found) ? found : null;
        }

        private BitmapSource? LoadFrameForAsset(Asset asset, Segment segment, double playheadSeconds)
        {
            if (string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
                return null;

            if (string.Equals(asset.Type, "Image", StringComparison.OrdinalIgnoreCase))
            {
                var key = BuildAssetFrameCacheKey(asset);
                if (_lastVisualSegmentId == segment.Id && _lastVisualFrameKey == key && ActiveVisualImage != null)
                    return ActiveVisualImage;

                var bmp = LoadBitmapFromPath(asset.FilePath, key);
                _lastVisualSegmentId = segment.Id;
                _lastVisualFrameKey = key;
                return bmp;
            }

            if (!string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                return null;

            var timeIntoVideo = Math.Max(0, playheadSeconds - segment.StartTime);
            var quantized = Math.Round(timeIntoVideo, 1); // ~10fps to avoid excessive FFmpeg calls
            var frameKey = $"{BuildAssetFrameCacheKey(asset)}|{quantized:F2}";

            if (_lastVisualSegmentId == segment.Id && _lastVisualFrameKey == frameKey && ActiveVisualImage != null)
                return ActiveVisualImage;

            if (_timelineViewModel?.IsDeferringThumbnailUpdate == true)
                return ActiveVisualImage;

            var thumbPath = FFmpegService.GetThumbnailCachePathFor(asset.FilePath, quantized);
            if (!File.Exists(thumbPath))
            {
                QueueVideoFrameGeneration(asset.FilePath, quantized, frameKey, segment.Id);
                return ActiveVisualImage;
            }

            var bmpFrame = LoadBitmapFromPath(thumbPath, frameKey);
            if (bmpFrame != null)
            {
                _lastVisualSegmentId = segment.Id;
                _lastVisualFrameKey = frameKey;
            }
            return bmpFrame;
        }

        private void QueueVideoFrameGeneration(string videoPath, double timeInVideo, string frameKey, string? segmentId)
        {
            lock (_pendingVideoFrameLock)
            {
                if (!_pendingVideoFrameRequests.Add(frameKey))
                    return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var generated = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(videoPath, timeInVideo).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(generated) || !File.Exists(generated))
                        return;

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var bmp = LoadBitmapFromPath(generated, frameKey);
                        if (bmp == null || ActiveVisualSegment == null)
                            return;

                        if (!string.Equals(ActiveVisualSegment.Id, segmentId, StringComparison.Ordinal))
                            return;

                        ActiveVisualImage = bmp;
                        _lastVisualSegmentId = segmentId;
                        _lastVisualFrameKey = frameKey;
                        IsVisualPlaceholderVisible = false;
                    });
                }
                catch
                {
                    // Ignore background thumbnail failures to keep scrubbing smooth.
                }
                finally
                {
                    lock (_pendingVideoFrameLock)
                        _pendingVideoFrameRequests.Remove(frameKey);
                }
            });
        }

        private BitmapSource? LoadBitmapFromPath(string path, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            // Check LRU cache first
            if (_assetFrameCache.TryGet(cacheKey, out var cached))
                return cached;

            try
            {
                var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = 768;
                bmp.EndInit();
                bmp.Freeze();

                // Add to LRU cache (auto-evicts if full)
                _assetFrameCache.Add(cacheKey, bmp);

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void InvalidateAssetLookup()
        {
            _assetDictionary = null;
            _assetDictionarySignature = 0;
        }

        private void ClearActiveVisualCache()
        {
            _lastVisualFrameKey = null;
            _lastVisualSegmentId = null;
        }

        private void EnsureAssetDictionaryCurrent(ICollection<Asset> assets)
        {
            var signature = ComputeAssetCollectionSignature(assets);
            if (_assetDictionary != null && _assetDictionarySignature == signature)
                return;

            _assetDictionary = new Dictionary<string, Asset>(assets.Count, StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (!string.IsNullOrWhiteSpace(asset.Id))
                    _assetDictionary[asset.Id] = asset;
            }

            _assetDictionarySignature = signature;
        }

        private static long ComputeAssetCollectionSignature(ICollection<Asset> assets)
        {
            unchecked
            {
                long sum = assets.Count;
                long xor = 0;

                foreach (var asset in assets)
                {
                    var hash = new HashCode();
                    hash.Add(asset.Id, StringComparer.Ordinal);
                    hash.Add(asset.FilePath, StringComparer.OrdinalIgnoreCase);
                    hash.Add(asset.Type, StringComparer.OrdinalIgnoreCase);
                    hash.Add(asset.FileSize);
                    hash.Add(asset.Width);
                    hash.Add(asset.Height);
                    hash.Add(asset.Duration);

                    var assetHash = hash.ToHashCode();
                    sum += assetHash;
                    xor ^= assetHash;
                }

                return (sum * 397) ^ xor;
            }
        }

        private static string BuildAssetFrameCacheKey(Asset asset)
            => string.Join("|",
                asset.Id ?? string.Empty,
                asset.FilePath ?? string.Empty,
                asset.Type ?? string.Empty,
                asset.FileSize,
                asset.Width?.ToString() ?? string.Empty,
                asset.Height?.ToString() ?? string.Empty);
    }
}
