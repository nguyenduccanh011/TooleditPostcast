// CanvasViewModel.AssetCache.cs — Asset lookup dictionary and LRU frame cache.
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;

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

        private void InvalidateAssetLookup()
        {
            _assetDictionary = null;
            _assetDictionarySignature = 0;
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

    }
}
