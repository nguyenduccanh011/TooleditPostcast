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

    private async void AddBgmButton_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        var project = mainVm?.ProjectViewModel?.CurrentProject;
        if (project == null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select BGM Audio File",
            Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma|All Files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var bgm = new BgmTrack
            {
                ProjectId = project.Id,
                Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
                AudioPath = dialog.FileName,
                Volume = 0.3,
                FadeInSeconds = 2.0,
                FadeOutSeconds = 2.0,
                IsEnabled = true
            };

            project.BgmTracks.Add(bgm);
            await mainVm!.ProjectViewModel.SaveProjectAsync();

            // Refresh binding
            BgmTracksList.ItemsSource = null;
            BgmTracksList.ItemsSource = project.BgmTracks;
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Error(ex, "Error adding BGM track");
        }
    }

    private async void RemoveBgmButton_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as MainViewModel;
        var project = mainVm?.ProjectViewModel?.CurrentProject;
        if (project == null)
            return;

        if (sender is not Button btn || btn.Tag is not string bgmId)
            return;

        var bgm = project.BgmTracks.FirstOrDefault(b => b.Id == bgmId);
        if (bgm != null)
        {
            project.BgmTracks.Remove(bgm);
            await mainVm!.ProjectViewModel.SaveProjectAsync();

            BgmTracksList.ItemsSource = null;
            BgmTracksList.ItemsSource = project.BgmTracks;
        }
    }
}
