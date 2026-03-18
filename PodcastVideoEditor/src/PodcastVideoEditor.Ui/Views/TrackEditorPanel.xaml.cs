using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.ViewModels;
using Serilog;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Properties panel for a selected Track.
/// DataContext = TimelineViewModel; shows SelectedTrack properties.
/// </summary>
public partial class TrackEditorPanel : UserControl
{
    private TimelineViewModel? _viewModel;
    private Track? _subscribedTrack;
    private bool _suppressRadioChecked;

    public TrackEditorPanel()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as TimelineViewModel;
        if (_viewModel == null) return;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        RefreshPanel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        if (_subscribedTrack != null)
        {
            _subscribedTrack.PropertyChanged -= Track_PropertyChanged;
            _subscribedTrack = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.SelectedTrack))
            RefreshPanel();
    }

    private void RefreshPanel()
    {
        // Unsubscribe from old track
        if (_subscribedTrack != null)
        {
            _subscribedTrack.PropertyChanged -= Track_PropertyChanged;
            _subscribedTrack = null;
        }

        var track = _viewModel?.SelectedTrack;

        if (track == null)
        {
            NoSelectionText.Visibility = Visibility.Visible;
            PropertiesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Subscribe to track property changes (e.g. IsLocked/IsVisible toggled externally)
        _subscribedTrack = track;
        track.PropertyChanged += Track_PropertyChanged;

        NoSelectionText.Visibility = Visibility.Collapsed;
        PropertiesPanel.Visibility = Visibility.Visible;

        // Populate fields
        TrackNameText.Text = string.IsNullOrWhiteSpace(track.Name) ? "(no name)" : track.Name;
        TrackTypeText.Text = track.TrackType;

        // Show image layout section only for visual tracks
        bool isVisual = string.Equals(track.TrackType, "visual", StringComparison.OrdinalIgnoreCase);
        ImageLayoutSection.Visibility = isVisual ? Visibility.Visible : Visibility.Collapsed;

        if (isVisual)
            SyncRadioButtons(track.ImageLayoutPreset);

        UpdateLockVisibilityButtons(track);

        Log.Debug("TrackEditorPanel: showing track '{Name}' ({Type})", track.Name, track.TrackType);
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Track track) return;
        if (e.PropertyName == nameof(Track.IsLocked) || e.PropertyName == nameof(Track.IsVisible))
            UpdateLockVisibilityButtons(track);
        if (e.PropertyName == nameof(Track.ImageLayoutPreset))
            SyncRadioButtons(track.ImageLayoutPreset);
    }

    private void SyncRadioButtons(string preset)
    {
        _suppressRadioChecked = true;
        RadioFullFrame.IsChecked       = preset == ImageLayoutPresets.FullFrame;
        RadioSquareCenter.IsChecked    = preset == ImageLayoutPresets.Square_Center;
        RadioWidescreenCenter.IsChecked = preset == ImageLayoutPresets.Widescreen_Center;
        _suppressRadioChecked = false;

        PresetHintText.Text = preset switch
        {
            ImageLayoutPresets.Square_Center     => "Ảnh 1:1 căn giữa — phần trên/dưới dành cho tiêu đề và subtitle.",
            ImageLayoutPresets.Widescreen_Center => "Ảnh 16:9 letterbox căn giữa trong frame dọc.",
            _                                    => "Ảnh phủ toàn khung, crop nếu cần."
        };
    }

    private void UpdateLockVisibilityButtons(Track track)
    {
        LockBtnText.Text  = track.IsLocked  ? "🔒 Đã khóa" : "🔓 Mở khóa";
        VisBtnText.Text   = track.IsVisible ? "👁 Hiện"    : "🚫 Ẩn";
    }

    private void RadioPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressRadioChecked) return;
        if (_viewModel?.SelectedTrack == null) return;
        if (sender is RadioButton rb && rb.Tag is string preset)
        {
            _viewModel.SelectedTrack.ImageLayoutPreset = preset;
            SyncRadioButtons(preset);
            _viewModel.RequestProjectSave();
            Log.Information("Track '{Name}' ImageLayoutPreset changed to '{Preset}'",
                _viewModel.SelectedTrack.Name, preset);
        }
    }

    private void LockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedTrack != null)
            _viewModel.ToggleTrackLock(_viewModel.SelectedTrack);
    }

    private void VisibilityBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedTrack != null)
            _viewModel.ToggleTrackVisibility(_viewModel.SelectedTrack);
    }
}
