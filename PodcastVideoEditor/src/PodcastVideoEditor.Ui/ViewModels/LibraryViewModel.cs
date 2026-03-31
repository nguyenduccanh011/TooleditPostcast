#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// ViewModel for the global asset library panel.
/// Exposes browsing, filtering, importing, and deleting library assets.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly GlobalAssetService _globalAssetService;

    public ObservableCollection<GlobalAsset> Assets { get; } = new();
    public ObservableCollection<string> Categories { get; } = new() { "All" };

    [ObservableProperty]
    private string selectedCategory = "All";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public LibraryViewModel(GlobalAssetService globalAssetService)
    {
        _globalAssetService = globalAssetService;
    }

    partial void OnSelectedCategoryChanged(string value) => _ = RefreshAsync();
    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    /// <summary>
    /// Load/refresh the asset list from the database.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            var items = await _globalAssetService.GetAssetsAsync(SelectedCategory, SearchText);
            Assets.Clear();
            foreach (var item in items)
                Assets.Add(item);

            // Refresh category list
            var cats = await _globalAssetService.GetCategoriesAsync();
            Categories.Clear();
            Categories.Add("All");
            foreach (var c in cats)
            {
                if (!Categories.Contains(c))
                    Categories.Add(c);
            }
            // Always show all standard categories for import purposes
            foreach (var c in GlobalAssetService.Categories)
            {
                if (!Categories.Contains(c))
                    Categories.Add(c);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error refreshing library assets");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Import one or more files into the global library.
    /// </summary>
    public async Task<GlobalAsset?> ImportFileAsync(string filePath, string category = "Uncategorized")
    {
        try
        {
            var asset = await _globalAssetService.ImportAsync(filePath, category);
            // Add to current view if matches filter
            if (SelectedCategory == "All" || SelectedCategory == category)
                Assets.Add(asset);
            StatusMessage = $"Imported: {asset.Name}";
            return asset;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error importing file to library: {Path}", filePath);
            StatusMessage = $"Import error: {ex.Message}";
            return null;
        }
    }

    [RelayCommand]
    private async Task DeleteAsset(GlobalAsset? asset)
    {
        if (asset == null || asset.IsBuiltIn)
            return;

        var deleted = await _globalAssetService.DeleteAsync(asset.Id);
        if (deleted)
        {
            Assets.Remove(asset);
            StatusMessage = $"Deleted: {asset.Name}";
        }
    }
}
