using Microsoft.Win32;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.ViewModels;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;
using System.Globalization;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Left column of CapCut-like editor: Tab-based panel with Media, Audio, Text, Script tabs.
/// Supports drag-and-drop of assets to timeline tracks.
/// DataContext should be MainViewModel (ProjectViewModel, TimelineViewModel, CanvasViewModel).
/// </summary>
public partial class ElementsMediaPanelView : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragging;
    private ProjectViewModel? _subscribedProjectVm;

    public ElementsMediaPanelView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, __) => ApplyAssetFiltersAndSubscribe();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAssetFiltersAndSubscribe();
    }

    /// <summary>
    /// Subscribe to ProjectViewModel.CurrentProject changes (once per VM instance)
    /// and apply asset filters. Called on Loaded and on DataContext changes.
    /// </summary>
    private void ApplyAssetFiltersAndSubscribe()
    {
        var mainVm = DataContext as MainViewModel;
        var newProjectVm = mainVm?.ProjectViewModel;

        // Wire project-change subscription, avoiding duplicate handlers
        if (newProjectVm != null && newProjectVm != _subscribedProjectVm)
        {
            if (_subscribedProjectVm != null)
                _subscribedProjectVm.PropertyChanged -= OnProjectViewModelPropertyChanged;
            newProjectVm.PropertyChanged += OnProjectViewModelPropertyChanged;
            _subscribedProjectVm = newProjectVm;
        }

        ApplyAssetFilters();
    }

    private void OnProjectViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectViewModel.CurrentProject))
            ApplyAssetFilters();
    }

    private void ApplyAssetFilters()
    {
        // Filter MediaAssetsList to Image/Video only
        if (MediaAssetsList.ItemsSource != null)
        {
            var mediaView = CollectionViewSource.GetDefaultView(MediaAssetsList.ItemsSource);
            if (mediaView != null)
            {
                mediaView.Filter = obj =>
                {
                    if (obj is Asset asset)
                    {
                        return string.Equals(asset.Type, "Image", System.StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(asset.Type, "Video", System.StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                };
            }
        }

        UpdateNoMediaTextVisibility();
    }

    /// <summary>
    /// Show NoMediaText when the media (Image/Video) list is empty after filtering.
    /// </summary>
    private void UpdateNoMediaTextVisibility()
    {
        if (NoMediaText == null || MediaAssetsList?.ItemsSource == null)
            return;
        var view = CollectionViewSource.GetDefaultView(MediaAssetsList.ItemsSource);
        NoMediaText.Visibility = (view == null || !view.IsEmpty)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AssetItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void AssetItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(null);
        var diff = currentPos - _dragStartPoint;

        if (System.Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging)
            return;

        if (sender is not FrameworkElement element)
            return;

        var asset = element.Tag as Asset ?? element.DataContext as Asset;
        if (asset == null)
            return;

        _isDragging = true;

        var data = new DataObject("PVE_Asset", asset);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);

        _isDragging = false;
    }

    // ─── Element Drag-and-Drop (Text presets, Visualizer → Timeline) ───

    private void ElementItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void ElementItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(null);
        var diff = currentPos - _dragStartPoint;

        if (System.Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging)
            return;

        if (sender is not FrameworkElement element)
            return;

        var tag = element.Tag as string;
        if (string.IsNullOrEmpty(tag))
            return;

        _isDragging = true;

        var data = new DataObject("PVE_ElementType", tag);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);

        _isDragging = false;
    }

    /// <summary>
    /// If user clicks without dragging, create element at playhead (fallback).
    /// </summary>
    private void ElementItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
            return;

        if (sender is not FrameworkElement element)
            return;

        var tag = element.Tag as string;
        if (string.IsNullOrEmpty(tag))
            return;

        var mainVm = DataContext as MainViewModel;
        var canvasVm = mainVm?.CanvasViewModel;
        if (canvasVm == null)
            return;

        if (tag == "Visualizer")
            canvasVm.AddVisualizerElementAt();
        else if (System.Enum.TryParse<PodcastVideoEditor.Core.Models.TextStyle>(tag, out var preset))
            canvasVm.AddTextElementWithPreset(preset);
    }

    private async void ImportMediaButton_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm?.ProjectViewModel?.CurrentProject == null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import Hình ảnh / Video",
            Filter = "Images & Videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.mkv;*.avi;*.webm|All Files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        foreach (var filePath in dialog.FileNames)
        {
            try
            {
                var assetType = InferMediaAssetType(filePath);
                var asset = await mainVm.ProjectViewModel.AddAssetToCurrentProjectAsync(filePath, assetType);
                if (asset == null)
                    Serilog.Log.Warning("Import media asset returned null for {FilePath}", filePath);
            }
            catch (System.Exception ex)
            {
                Serilog.Log.Error(ex, "Error importing media asset {FilePath}", filePath);
                MessageBox.Show(ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Refresh filter + empty-state visibility
        ApplyAssetFilters();
        UpdateNoMediaTextVisibility();
    }

    private static string InferMediaAssetType(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).Trim('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp" => "Image",
            "mp4" or "mov" or "mkv" or "avi" or "webm" => "Video",
            _ => "Image"
        };
    }

    private async void ImportAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm?.ProjectViewModel?.CurrentProject == null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import Audio File",
            Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma|All Files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var asset = await mainVm.ProjectViewModel.AddAssetToCurrentProjectAsync(dialog.FileName, "Audio");
            if (asset == null)
            {
                MessageBox.Show(mainVm.ProjectViewModel.StatusMessage, "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Error(ex, "Error importing audio asset");
            MessageBox.Show(ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Library Tab (Global Asset Library) ───

    private Point _libraryDragStartPoint;
    private bool _isLibraryDragging;

    private void LibraryAssetItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _libraryDragStartPoint = e.GetPosition(null);
        _isLibraryDragging = false;

        // Double-click → add to current project
        if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.Tag is GlobalAsset globalAsset)
        {
            _ = AddGlobalAssetToProjectAsync(globalAsset);
        }
    }

    private void LibraryAssetItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(null);
        var diff = currentPos - _libraryDragStartPoint;

        if (System.Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isLibraryDragging)
            return;

        if (sender is not FrameworkElement element)
            return;

        var globalAsset = element.Tag as GlobalAsset;
        if (globalAsset == null)
            return;

        _isLibraryDragging = true;

        var data = new DataObject("PVE_GlobalAsset", globalAsset);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);

        _isLibraryDragging = false;
    }

    private void LibraryAssetItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not GlobalAsset globalAsset)
            return;

        var menu = new ContextMenu();

        var addItem = new MenuItem { Header = "Thêm vào project" };
        addItem.Click += (_, __) => _ = AddGlobalAssetToProjectAsync(globalAsset);
        menu.Items.Add(addItem);

        if (!globalAsset.IsBuiltIn)
        {
            var deleteItem = new MenuItem { Header = "Xóa khỏi thư viện" };
            deleteItem.Click += async (_, __) =>
            {
                var result = MessageBox.Show(
                    $"Xóa \"{globalAsset.Name}\" khỏi thư viện?",
                    "Xác nhận xóa",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    var mainVm = DataContext as MainViewModel;
                    if (mainVm?.LibraryViewModel != null)
                        await mainVm.LibraryViewModel.DeleteAssetCommand.ExecuteAsync(globalAsset);
                }
            };
            menu.Items.Add(deleteItem);
        }

        element.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Copy a global library asset into the current project (copy-on-use pattern).
    /// </summary>
    private async System.Threading.Tasks.Task AddGlobalAssetToProjectAsync(GlobalAsset globalAsset)
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm?.ProjectViewModel?.CurrentProject == null)
        {
            MessageBox.Show("Hãy mở một project trước.", "Chưa có project", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Check if this global asset is already in the project (by GlobalAssetId)
        var existing = mainVm.ProjectViewModel.CurrentProject.Assets
            .FirstOrDefault(a => a.GlobalAssetId == globalAsset.Id);
        if (existing != null)
        {
            Serilog.Log.Information("Global asset {Id} already in project, reusing {AssetId}", globalAsset.Id, existing.Id);
            return;
        }

        // Copy-on-use: import file into project
        if (!System.IO.File.Exists(globalAsset.FilePath))
        {
            MessageBox.Show($"Tệp không tìm thấy: {globalAsset.FilePath}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var asset = await mainVm.ProjectViewModel.AddAssetToCurrentProjectAsync(globalAsset.FilePath, "Image");
            if (asset != null)
            {
                asset.GlobalAssetId = globalAsset.Id;
                await mainVm.ProjectViewModel.SaveProjectAsync();
            }
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Error(ex, "Error adding global asset to project");
            MessageBox.Show(ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        ApplyAssetFilters();
    }

    private async void ImportLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        if (mainVm?.LibraryViewModel == null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import ảnh vào thư viện",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        // Determine selected category from the import combo
        var category = "Uncategorized";
        if (ImportCategoryCombo?.SelectedItem is string selectedCat && selectedCat != "All")
            category = selectedCat;

        foreach (var filePath in dialog.FileNames)
        {
            await mainVm.LibraryViewModel.ImportFileAsync(filePath, category);
        }
    }

}
