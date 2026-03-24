#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Services;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels;

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
    [ObservableProperty] private bool isLoadingYesScaleModels;
    [ObservableProperty] private string yesScaleModelStatus = "Enter a YesScale API key to load models.";

    // ── Image Search ─────────────────────────────────────────────────────────
    [ObservableProperty] private string pexelsApiKey   = string.Empty;
    [ObservableProperty] private string pixabayApiKey  = string.Empty;
    [ObservableProperty] private string unsplashApiKey = string.Empty;

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string statusMessage  = string.Empty;
    [ObservableProperty] private bool   isStatusError;

    public ObservableCollection<string> AvailableYesScaleModels { get; } = new();

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
            _store.YesScaleApiKey  = YesScaleApiKey.Trim();
            _store.YesScaleBaseUrl = string.IsNullOrWhiteSpace(YesScaleBaseUrl)
                ? "https://api.yescale.vip/v1"
                : YesScaleBaseUrl.Trim();
            _store.YesScaleModel   = string.IsNullOrWhiteSpace(YesScaleModel)
                ? "gpt-4o-mini"
                : YesScaleModel.Trim();
            _store.PexelsApiKey    = PexelsApiKey.Trim();
            _store.PixabayApiKey   = PixabayApiKey.Trim();
            _store.UnsplashApiKey  = UnsplashApiKey.Trim();
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

    private void LoadFromStore()
    {
        YesScaleApiKey  = _store.YesScaleApiKey;
        YesScaleBaseUrl = _store.YesScaleBaseUrl;
        YesScaleModel   = _store.YesScaleModel;
        PexelsApiKey    = _store.PexelsApiKey;
        PixabayApiKey   = _store.PixabayApiKey;
        UnsplashApiKey  = _store.UnsplashApiKey;
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

            var apiKey = YesScaleApiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                AvailableYesScaleModels.Clear();
                YesScaleModelStatus = "Enter a YesScale API key to load models.";
                IsLoadingYesScaleModels = false;
                return;
            }

            IsLoadingYesScaleModels = true;
            YesScaleModelStatus = "Loading models from YesScale...";

            var modelIds = await _aiProvider.GetAvailableModelsAsync(
                apiKey,
                NormalizeBaseUrl(YesScaleBaseUrl),
                ct);

            if (ct.IsCancellationRequested)
                return;

            AvailableYesScaleModels.Clear();
            foreach (var modelId in modelIds)
                AvailableYesScaleModels.Add(modelId);

            if (AvailableYesScaleModels.Count == 0)
            {
                YesScaleModelStatus = "No models returned for this API key.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(YesScaleModel) || !AvailableYesScaleModels.Contains(YesScaleModel))
                    YesScaleModel = AvailableYesScaleModels.First();

                YesScaleModelStatus = $"Loaded {AvailableYesScaleModels.Count} model(s).";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AvailableYesScaleModels.Clear();
            YesScaleModelStatus = $"Could not load models: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoadingYesScaleModels = false;
        }
    }

    private static string NormalizeBaseUrl(string baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.yescale.vip/v1"
            : baseUrl.Trim().TrimEnd('/');
}
