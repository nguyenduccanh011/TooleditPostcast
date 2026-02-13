using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PodcastVideoEditor.Ui.ViewModels;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Unified properties panel: shows Segment properties or Element properties based on selection.
/// Priority: Segment (timeline) > Element (canvas). DataContext should be MainViewModel.
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
            if (args.PropertyName == nameof(TimelineViewModel.SelectedSegment))
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
        var hasElement = _mainViewModel.CanvasViewModel.SelectedElement != null;

        // Priority: segment > element > none
        if (hasSegment)
        {
            NoSelectionText.Visibility = Visibility.Collapsed;
            ElementPanel.Visibility = Visibility.Collapsed;
            SegmentPanel.Visibility = Visibility.Visible;
        }
        else if (hasElement)
        {
            NoSelectionText.Visibility = Visibility.Collapsed;
            ElementPanel.Visibility = Visibility.Visible;
            SegmentPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoSelectionText.Visibility = Visibility.Visible;
            ElementPanel.Visibility = Visibility.Collapsed;
            SegmentPanel.Visibility = Visibility.Collapsed;
        }
    }
}
