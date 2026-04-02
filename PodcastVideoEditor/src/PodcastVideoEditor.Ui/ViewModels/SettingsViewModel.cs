#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// A model ID enriched with its source profile info, used for grouped display.
/// </summary>
public sealed partial class ModelDisplayItem : ObservableObject
{
    public string ModelId     { get; init; } = string.Empty;
    public string ProfileId   { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string Display     => ModelId;
    public string Group       => $"{ProfileName} ({ProfileId})";

    [ObservableProperty] private bool isInFallback;

    public override string ToString() => ModelId;
}

/// <summary>
/// Display wrapper for a <see cref="ModelFallbackEntry"/> that includes the resolved profile name.
/// </summary>
public sealed class FallbackEntryDisplay
{
    public string ModelId     { get; init; } = string.Empty;
    public string ProfileId   { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string Display     => string.IsNullOrEmpty(ProfileName) ? ModelId : $"{ModelId}  [{ProfileName}]";

    public ModelFallbackEntry ToEntry() => new() { ModelId = ModelId, ProfileId = ProfileId };
    public override string ToString() => Display;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsStore _store;
    private readonly IAIProvider _aiProvider;
    private readonly string _appDataPath;
    private CancellationTokenSource? _loadModelsCts;

    // ── YesScale ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string yesScaleApiKey   = string.Empty;
    [ObservableProperty] private string yesScaleBaseUrl  = string.Empty;
    [ObservableProperty] private string yesScaleModel    = string.Empty;
    [ObservableProperty] private string ffmpegPath       = string.Empty;
    [ObservableProperty] private bool isLoadingYesScaleModels;
    [ObservableProperty] private string yesScaleModelStatus = "Enter a YesScale API key to load models.";

    // ── API Key Profiles ─────────────────────────────────────────────────────
    public ObservableCollection<ApiKeyProfile> Profiles { get; } = new();
    [ObservableProperty] private ApiKeyProfile? selectedProfile;
    [ObservableProperty] private string newProfileName   = string.Empty;
    [ObservableProperty] private string newProfileApiKey  = string.Empty;

    // ── Fallback entries ─────────────────────────────────────────────────────
    public ObservableCollection<FallbackEntryDisplay> FallbackEntries { get; } = new();
    [ObservableProperty] private FallbackEntryDisplay? selectedFallbackEntry;
    [ObservableProperty] private ApiKeyProfile? selectedFallbackProfile;

    // ── Fallback model picker (separate from primary) ────────────────────────
    [ObservableProperty] private string fallbackModelFilter = string.Empty;

    // ── Image Search ─────────────────────────────────────────────────────────
    [ObservableProperty] private string pexelsApiKey   = string.Empty;
    [ObservableProperty] private string pixabayApiKey  = string.Empty;
    [ObservableProperty] private string unsplashApiKey = string.Empty;

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string statusMessage  = string.Empty;
    [ObservableProperty] private bool   isStatusError;

    public ObservableCollection<string> AvailableYesScaleModels { get; } = new();

    /// <summary>Grouped model list: each ModelDisplayItem carries its source profile info.</summary>
    public ObservableCollection<ModelDisplayItem> GroupedModels { get; } = new();

    /// <summary>Models filtered by the selected fallback profile, for the fallback picker ComboBox.</summary>
    public ObservableCollection<string> FallbackAvailableModels { get; } = new();

    /// <summary>Selected model in the fallback picker ComboBox.</summary>
    [ObservableProperty] private string? selectedFallbackModel;

    /// <summary>Per-profile model mapping built during refresh.</summary>
    private Dictionary<string, List<string>> _profileModelMap = new();

    public SettingsViewModel(UserSettingsStore store, IAIProvider aiProvider, string appDataPath)
    {
        _store       = store;
        _aiProvider  = aiProvider;
        _appDataPath = appDataPath;
        LoadFromStore();
        QueueYesScaleModelReload();
    }

    partial void OnYesScaleApiKeyChanged(string value) => QueueYesScaleModelReload();

    partial void OnYesScaleBaseUrlChanged(string value) => QueueYesScaleModelReload();

    // ── Profile CRUD ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddProfile()
    {
        var name = NewProfileName.Trim();
        var key  = NewProfileApiKey.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) return;

        var profile = new ApiKeyProfile
        {
            Id      = Guid.NewGuid().ToString("N")[..8],
            Name    = name,
            ApiKey  = key,
            Enabled = true
        };
        Profiles.Add(profile);
        NewProfileName  = string.Empty;
        NewProfileApiKey = string.Empty;

        // Sync primary key from the first profile so API calls use it
        SyncPrimaryKeyFromProfiles();

        // Reload models to include models from the new profile
        QueueYesScaleModelReload();
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile == null) return;
        var id = SelectedProfile.Id;
        Profiles.Remove(SelectedProfile);
        // Remove fallback entries that reference this profile
        var toRemove = FallbackEntries.Where(f => f.ProfileId == id).ToList();
        foreach (var e in toRemove) FallbackEntries.Remove(e);

        // Keep primary key in sync with remaining profiles
        SyncPrimaryKeyFromProfiles();
        QueueYesScaleModelReload();
    }

    // ── Fallback entry CRUD ──────────────────────────────────────────────────

    /// <summary>When fallback profile changes, filter models to only those belonging to that profile.</summary>
    partial void OnSelectedFallbackProfileChanged(ApiKeyProfile? value)
    {
        FallbackAvailableModels.Clear();
        SelectedFallbackModel = null;
        if (value == null) return;

        if (_profileModelMap.TryGetValue(value.Id, out var models))
        {
            foreach (var m in models.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                FallbackAvailableModels.Add(m);
        }
    }

    [RelayCommand]
    private void AddFallbackEntry()
    {
        // Use the separate fallback picker model, NOT the primary model
        var modelId = SelectedFallbackModel;
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var profile = SelectedFallbackProfile ?? Profiles.FirstOrDefault();
        if (profile == null) return;

        // Don't add exact duplicates (same model + same profile)
        if (FallbackEntries.Any(e =>
            e.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) &&
            e.ProfileId == profile.Id))
            return;

        FallbackEntries.Add(new FallbackEntryDisplay
        {
            ModelId     = modelId,
            ProfileId   = profile.Id,
            ProfileName = profile.Name
        });

        // Sync the star indicators on the grouped model list
        SyncFallbackStars();
    }

    /// <summary>Toggle a model in/out of the fallback chain directly from the grouped model list.</summary>
    [RelayCommand]
    private void ToggleFallback(ModelDisplayItem? item)
    {
        if (item == null) return;

        var existing = FallbackEntries.FirstOrDefault(e =>
            e.ModelId.Equals(item.ModelId, StringComparison.OrdinalIgnoreCase) &&
            e.ProfileId == item.ProfileId);

        if (existing != null)
        {
            FallbackEntries.Remove(existing);
        }
        else
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == item.ProfileId);
            FallbackEntries.Add(new FallbackEntryDisplay
            {
                ModelId     = item.ModelId,
                ProfileId   = item.ProfileId,
                ProfileName = profile?.Name ?? item.ProfileName
            });
        }

        SyncFallbackStars();
    }

    /// <summary>Update IsInFallback flags on all GroupedModels to reflect current FallbackEntries.</summary>
    private void SyncFallbackStars()
    {
        foreach (var m in GroupedModels)
        {
            m.IsInFallback = FallbackEntries.Any(e =>
                e.ModelId.Equals(m.ModelId, StringComparison.OrdinalIgnoreCase) &&
                e.ProfileId == m.ProfileId);
        }
    }

    [RelayCommand]
    private void RemoveFallbackEntry()
    {
        if (SelectedFallbackEntry != null)
        {
            FallbackEntries.Remove(SelectedFallbackEntry);
            SyncFallbackStars();
        }
    }

    [RelayCommand]
    private void MoveFallbackEntryUp()
    {
        if (SelectedFallbackEntry == null) return;
        var idx = FallbackEntries.IndexOf(SelectedFallbackEntry);
        if (idx > 0) FallbackEntries.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveFallbackEntryDown()
    {
        if (SelectedFallbackEntry == null) return;
        var idx = FallbackEntries.IndexOf(SelectedFallbackEntry);
        if (idx >= 0 && idx < FallbackEntries.Count - 1) FallbackEntries.Move(idx, idx + 1);
    }

    [RelayCommand]
    private async Task RefreshYesScaleModelsAsync()
    {
        _loadModelsCts?.Cancel();
        _loadModelsCts?.Dispose();
        _loadModelsCts = new CancellationTokenSource();
        await LoadYesScaleModelsAsync(_loadModelsCts.Token, skipDebounce: true);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            FlushToStore();
            _store.Save(_appDataPath);
            LoadFromStore();
            IsStatusError = false;
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"Error saving settings: {ex.Message}";
        }
    }

    /// <summary>Exports current settings to the specified file path.</summary>
    public void ExportSettings(string filePath)
    {
        try
        {
            FlushToStore();
            _store.ExportTo(filePath);
            IsStatusError = false;
            StatusMessage = $"Settings exported to {Path.GetFileName(filePath)}.";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Imports settings from the specified JSON file and refreshes the UI.</summary>
    public void ImportSettings(string filePath)
    {
        try
        {
            if (_store.ImportFrom(filePath))
            {
                _store.Save(_appDataPath);
                LoadFromStore();
                QueueYesScaleModelReload();
                IsStatusError = false;
                StatusMessage = $"Settings imported from {Path.GetFileName(filePath)}.";
            }
            else
            {
                IsStatusError = true;
                StatusMessage = "Import failed: invalid settings file.";
            }
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private void LoadFromStore()
    {
        YesScaleApiKey  = _store.YesScaleApiKey;
        YesScaleBaseUrl = _store.YesScaleBaseUrl;
        YesScaleModel   = _store.YesScaleModel;
        FfmpegPath      = _store.FFmpegPath;
        PexelsApiKey    = _store.PexelsApiKey;
        PixabayApiKey   = _store.PixabayApiKey;
        UnsplashApiKey  = _store.UnsplashApiKey;

        Profiles.Clear();
        foreach (var p in _store.ApiKeyProfiles) Profiles.Add(p);

        // Ensure the fallback profile ComboBox is never empty when we have a primary key
        EnsurePrimaryProfile();

        FallbackEntries.Clear();
        foreach (var e in _store.FallbackEntries)
        {
            var profileName = Profiles.FirstOrDefault(p => p.Id == e.ProfileId)?.Name ?? "Primary";
            FallbackEntries.Add(new FallbackEntryDisplay
            {
                ModelId     = e.ModelId,
                ProfileId   = e.ProfileId,
                ProfileName = profileName
            });
        }
    }

    private void FlushToStore()
    {
        _store.YesScaleApiKey  = YesScaleApiKey.Trim();
        _store.YesScaleBaseUrl = string.IsNullOrWhiteSpace(YesScaleBaseUrl) ? "https://api.yescale.vip/v1" : YesScaleBaseUrl.Trim();
        _store.YesScaleModel   = string.IsNullOrWhiteSpace(YesScaleModel) ? "gpt-4o-mini" : YesScaleModel.Trim();
        _store.FFmpegPath      = FfmpegPath.Trim();
        _store.PexelsApiKey    = PexelsApiKey.Trim();
        _store.PixabayApiKey   = PixabayApiKey.Trim();
        _store.UnsplashApiKey  = UnsplashApiKey.Trim();

        // Ensure a "Primary" profile exists when user has a primary key but no explicit profiles
        EnsurePrimaryProfile();

        // Profiles
        _store.ApiKeyProfiles.Clear();
        foreach (var p in Profiles) _store.ApiKeyProfiles.Add(p);
        _store.PrimaryProfileId = Profiles.FirstOrDefault()?.Id ?? string.Empty;

        // Fallback entries
        _store.FallbackEntries.Clear();
        foreach (var e in FallbackEntries) _store.FallbackEntries.Add(e.ToEntry());
    }

    /// <summary>
    /// If the user has typed a primary API key but hasn't manually created any profiles,
    /// auto-create a "Primary" profile so fallback entries can reference it and the
    /// profile ComboBox is never empty. If the auto-profile already exists, keep its
    /// key in sync with the primary field.
    /// </summary>
    private void EnsurePrimaryProfile()
    {
        if (string.IsNullOrWhiteSpace(YesScaleApiKey))
            return;

        var trimmedKey = YesScaleApiKey.Trim();

        // If the auto-created "primary" profile exists, keep its key in sync
        var autoProfile = Profiles.FirstOrDefault(p => p.Id == "primary" || p.Id == "default");
        if (autoProfile != null)
        {
            if (autoProfile.ApiKey != trimmedKey)
            {
                var idx = Profiles.IndexOf(autoProfile);
                Profiles[idx] = autoProfile with { ApiKey = trimmedKey };
            }
            return;
        }

        // User has manually-created profiles — don't auto-insert
        if (Profiles.Count > 0) return;

        Profiles.Insert(0, new ApiKeyProfile
        {
            Id      = "primary",
            Name    = "Primary",
            ApiKey  = trimmedKey,
            Enabled = true
        });
    }

    private void QueueYesScaleModelReload()
    {
        _loadModelsCts?.Cancel();
        _loadModelsCts?.Dispose();
        _loadModelsCts = new CancellationTokenSource();
        _ = LoadYesScaleModelsAsync(_loadModelsCts.Token, skipDebounce: false);
    }

    private async Task LoadYesScaleModelsAsync(CancellationToken ct, bool skipDebounce)
    {
        try
        {
            if (!skipDebounce)
                await Task.Delay(700, ct);

            // Build list of (profileId, profileName, apiKey) to query
            EnsurePrimaryProfile();
            var profilesToQuery = Profiles
                .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ApiKey))
                .Select(p => (p.Id, p.Name, p.ApiKey))
                .GroupBy(p => p.ApiKey)
                .Select(g => g.First())
                .ToList();

            if (profilesToQuery.Count == 0)
            {
                AvailableYesScaleModels.Clear();
                GroupedModels.Clear();
                _profileModelMap.Clear();
                YesScaleModelStatus = "Enter a YesScale API key to load models.";
                IsLoadingYesScaleModels = false;
                return;
            }

            IsLoadingYesScaleModels = true;
            YesScaleModelStatus = "Loading models from YesScale...";

            var baseUrl = NormalizeBaseUrl(YesScaleBaseUrl);

            // Query all keys in parallel, keep results per profile
            var tasks = profilesToQuery.Select(async p =>
            {
                var models = await _aiProvider.GetAvailableModelsAsync(p.ApiKey, baseUrl, ct);
                return (p.Id, p.Name, Models: models);
            });
            var results = await Task.WhenAll(tasks);

            if (ct.IsCancellationRequested) return;

            // Build profile → models map
            var newMap = new Dictionary<string, List<string>>();
            foreach (var (profileId, profileName, models) in results)
            {
                newMap[profileId] = models
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            _profileModelMap = newMap;

            // Flat deduplicated list (backward compat for primary model ComboBox)
            var allModels = results
                .SelectMany(r => r.Models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AvailableYesScaleModels.Clear();
            foreach (var modelId in allModels)
                AvailableYesScaleModels.Add(modelId);

            // Build grouped model list
            GroupedModels.Clear();
            foreach (var (profileId, profileName, models) in results)
            {
                foreach (var modelId in models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                {
                    GroupedModels.Add(new ModelDisplayItem
                    {
                        ModelId     = modelId,
                        ProfileId   = profileId,
                        ProfileName = profileName
                    });
                }
            }

            // Sync star indicators
            SyncFallbackStars();

            // Refresh fallback profile filter if one is selected
            if (SelectedFallbackProfile != null)
                OnSelectedFallbackProfileChanged(SelectedFallbackProfile);

            if (AvailableYesScaleModels.Count == 0)
            {
                YesScaleModelStatus = "No models returned for the configured API keys.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(YesScaleModel) || !AvailableYesScaleModels.Contains(YesScaleModel))
                    YesScaleModel = AvailableYesScaleModels.First();

                var totalModels = GroupedModels.Count;
                var uniqueModels = AvailableYesScaleModels.Count;
                YesScaleModelStatus = $"Loaded {uniqueModels} unique model(s) from {profilesToQuery.Count} key(s) ({totalModels} total incl. overlaps).";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AvailableYesScaleModels.Clear();
            GroupedModels.Clear();
            _profileModelMap.Clear();
            YesScaleModelStatus = $"Could not load models: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoadingYesScaleModels = false;
        }
    }

    /// <summary>
    /// Keep YesScaleApiKey in sync with the first enabled profile's key.
    /// This ensures API calls always use a valid key without a separate "Primary Key" field.
    /// </summary>
    private void SyncPrimaryKeyFromProfiles()
    {
        var firstEnabled = Profiles.FirstOrDefault(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ApiKey));
        if (firstEnabled != null)
            YesScaleApiKey = firstEnabled.ApiKey;
        else if (Profiles.Count == 0)
            YesScaleApiKey = string.Empty;
    }

    private static string NormalizeBaseUrl(string baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.yescale.vip/v1"
            : baseUrl.Trim().TrimEnd('/');
}
