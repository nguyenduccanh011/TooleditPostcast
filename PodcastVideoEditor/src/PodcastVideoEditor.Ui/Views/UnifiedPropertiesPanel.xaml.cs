using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PodcastVideoEditor.Ui.ViewModels;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Unified properties panel: element-centric design (like CapCut/Premiere).
/// When a segment has a linked element → show element properties only (timing shown as badge).
/// SegmentEditorPanel is a fallback for segments without linked elements (e.g. bare audio).
/// DataContext should be MainViewModel.
/// </summary>
public partial class UnifiedPropertiesPanel : UserControl
{
    private MainViewModel? _mainViewModel;
    private PropertyChangedEventHandler? _timelineHandler;
    private PropertyChangedEventHandler? _canvasHandler;

    public UnifiedPropertiesPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _mainViewModel = DataContext as MainViewModel;
        if (_mainViewModel == null)
            return;

        _timelineHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.SelectedSegment) ||
                args.PropertyName == nameof(TimelineViewModel.SelectedTrack))
                UpdateVisibility();
        };
        _canvasHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(CanvasViewModel.SelectedElement))
                UpdateVisibility();
        };

        _mainViewModel.TimelineViewModel.PropertyChanged += _timelineHandler;
        _mainViewModel.CanvasViewModel.PropertyChanged += _canvasHandler;
        UpdateVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            if (_timelineHandler != null)
                _mainViewModel.TimelineViewModel.PropertyChanged -= _timelineHandler;
            if (_canvasHandler != null)
                _mainViewModel.CanvasViewModel.PropertyChanged -= _canvasHandler;
        }
        _mainViewModel = null;
        _timelineHandler = null;
        _canvasHandler = null;
    }

    private void UpdateVisibility()
    {
        if (_mainViewModel == null)
            return;

        var hasSegment = _mainViewModel.TimelineViewModel.SelectedSegment != null;
        var hasTrack   = _mainViewModel.TimelineViewModel.SelectedTrack != null && !hasSegment;
        var hasElement = _mainViewModel.CanvasViewModel.SelectedElement != null;

        // Reset all
        NoSelectionText.Visibility = Visibility.Collapsed;
        TrackPanel.Visibility      = Visibility.Collapsed;
        SegmentPanel.Visibility    = Visibility.Collapsed;
        ElementPanel.Visibility    = Visibility.Collapsed;

        if (hasElement)
        {
            // Element selected (via canvas or linked segment) → show element properties only.
            // Timing is shown as a badge inside PropertyEditorView (SegmentTimingText).
            // This matches CapCut/Premiere: properties panel shows only the element's properties.
            ElementPanel.Visibility = Visibility.Visible;
        }
        else if (hasSegment)
        {
            // Segment without linked element (e.g. bare audio/visual) → show segment fallback panel
            SegmentPanel.Visibility = Visibility.Visible;
        }
        else if (hasTrack)
        {
            TrackPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NoSelectionText.Visibility = Visibility.Visible;
        }
    }
}
