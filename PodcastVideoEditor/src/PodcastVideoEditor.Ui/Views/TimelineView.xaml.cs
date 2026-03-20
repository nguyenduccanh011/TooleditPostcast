using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Converters;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Cached brushes for segment selection state (avoid allocations on every update).
    /// </summary>
    internal static class SegmentSelectionBrushes
    {
        internal static readonly SolidColorBrush SelectedFill;
        internal static readonly SolidColorBrush NormalFill;
        internal static readonly SolidColorBrush SelectedBorder;
        internal static readonly SolidColorBrush NormalBorder;

        static SegmentSelectionBrushes()
        {
            SelectedFill = new SolidColorBrush(Color.FromRgb(0x35, 0x4a, 0x5f));
            NormalFill = new SolidColorBrush(Color.FromRgb(0x2d, 0x3a, 0x4a));
            SelectedBorder = new SolidColorBrush(Color.FromRgb(0x64, 0xb5, 0xf6));
            NormalBorder = new SolidColorBrush(Color.FromRgb(0x43, 0xa0, 0x47));
            SelectedFill.Freeze();
            NormalFill.Freeze();
            SelectedBorder.Freeze();
            NormalBorder.Freeze();
        }
    }

    /// <summary>
    /// Code-behind for TimelineView.xaml
    /// Handles drag-resize interactions and playhead sync.
    /// ST-9: Timeline Editor
    /// </summary>
    public partial class TimelineView : UserControl
    {
        private TimelineViewModel? _viewModel;
        private bool _isDraggingPlayhead;
        private bool _isResizingSegment;
        private bool _isResizingLeft;
        private bool _isDraggingSegment;
        private double _segmentOriginalEndTime;
        private double _segmentOriginalStartTime;
        private double _resizeDeltaX;
        private double _resizeLeftDeltaX;
        private double _resizeLeftOriginalStartTime;
        private double _moveDeltaX;
        // Pre-drag snapshots used for undo recording (never reset mid-drag unlike _segmentOriginal*).
        private double _dragUndoOriginalStart;
        private double _dragUndoOriginalEnd;
        private double _dragUndoOriginalSourceOffset;
        private EventHandler? _undoRedoStateChangedHandler;
        private readonly DispatcherTimer _zoomTimer;
        private double _pendingZoomFactor = 1.0;
        private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;
        private NotifyCollectionChangedEventHandler? _tracksCollectionChangedHandler;
        private DateTime _lastScrubPreviewTime = DateTime.MinValue;
        private const int ScrubPreviewThrottleMs = 16;

        public TimelineView()
        {
            InitializeComponent();
            Loaded += TimelineView_Loaded;
            Unloaded += TimelineView_Unloaded;

            _zoomTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _zoomTimer.Tick += ZoomTimer_Tick;
        }

        private void TimelineView_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as TimelineViewModel;

            if (_viewModel != null)
            {
                if (TimelineScroller != null)
                    TimelineScroller.PreviewMouseWheel += TimelineScroller_PreviewMouseWheel;

                // Timeline width is bound in XAML (RulerControl, track canvases, etc.)

                // Draw initial ruler
                InvalidateRuler();
                UpdateSegmentLayout();

                // Subscribe to property changes
                _viewModelPropertyChangedHandler = (s, args) =>
                {
                    if (args.PropertyName == nameof(TimelineViewModel.PlayheadPosition))
                    {
                        UpdatePlayheadPosition();
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.PixelsPerSecond))
                    {
                        InvalidateRuler();
                        UpdatePlayheadPosition();
                        UpdateSegmentLayout();
                        InvalidateWaveform();
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.TotalDuration))
                    {
                        InvalidateWaveform();
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.SelectedSegment))
                    {
                        UpdateSegmentSelection();
                        UpdateSegmentLayout(_viewModel.SelectedSegment);
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.TimelineWidth))
                    {
                        InvalidateRuler();
                        UpdateSegmentLayout();
                        InvalidateWaveform();
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.AudioPeaks))
                    {
                        InvalidateWaveform();
                    }
                };
                _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

                // Subscribe to tracks collection changes — defer layout so new item container exists
                _tracksCollectionChangedHandler = (s, args) =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                    {
                        if (IsLoaded)
                            UpdateSegmentLayout();
                    }));
                };
                _viewModel.Tracks.CollectionChanged += _tracksCollectionChangedHandler;

                // Subscribe to undo/redo state changes to refresh visual layout.
                if (_viewModel.UndoRedoService != null)
                {
                    _undoRedoStateChangedHandler = (_, _) =>
                    {
                        UpdateSegmentLayout();
                        UpdateSegmentSelection();
                    };
                    _viewModel.UndoRedoService.StateChanged += _undoRedoStateChangedHandler;
                }
            }
        }

        private void TimelineView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (TimelineScroller != null)
                TimelineScroller.PreviewMouseWheel -= TimelineScroller_PreviewMouseWheel;
            if (_viewModel != null && _viewModelPropertyChangedHandler != null)
                _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
            if (_viewModel != null && _tracksCollectionChangedHandler != null)
                _viewModel.Tracks.CollectionChanged -= _tracksCollectionChangedHandler;
            if (_viewModel?.UndoRedoService != null && _undoRedoStateChangedHandler != null)
                _viewModel.UndoRedoService.StateChanged -= _undoRedoStateChangedHandler;
            _zoomTimer.Stop();
        }

        /// <summary>
        /// Ctrl + mouse wheel: zoom timeline. Without Ctrl: normal scroll (pan).
        /// </summary>
        private void TimelineScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            if (_viewModel == null)
                return;
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            _pendingZoomFactor *= factor;
            _zoomTimer.Stop();
            _zoomTimer.Start();
            e.Handled = true;
        }

        private void ZoomTimer_Tick(object? sender, EventArgs e)
        {
            _zoomTimer.Stop();
            if (_viewModel == null)
                return;
            if (Math.Abs(_pendingZoomFactor - 1.0) < 0.0001)
                return;
            _viewModel.ZoomBy(_pendingZoomFactor);
            _pendingZoomFactor = 1.0;
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => _viewModel?.ZoomBy(1.5);
        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => _viewModel?.ZoomBy(1.0 / 1.5);
        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            // Reset to fit the entire timeline in the visible area
            if (TimelineScroller.ActualWidth > 0)
                _viewModel.TimelineWidth = TimelineScroller.ActualWidth - 56;
            else
                _viewModel.TimelineWidth = 800;
        }

        private void InvalidateRuler()
        {
            RulerControl?.InvalidateVisual();
        }

        private void InvalidateWaveform()
        {
            WaveformControl?.InvalidateVisual();
        }

        /// <summary>
        /// Update playhead position based on ViewModel's PlayheadPosition.
        /// </summary>
        private void UpdatePlayheadPosition()
        {
            if (_viewModel == null || PlayheadLine == null)
                return;

            double pixelX = _viewModel.TimeToPixels(_viewModel.PlayheadPosition);
            Canvas.SetLeft(PlayheadLine, pixelX);
        }

        /// <summary>
        /// Update segment block layout (position and size) based on timeline properties.
        /// Iterates through all track canvases and updates segment positioning within each track.
        /// </summary>
        private void UpdateSegmentLayout(Segment? limitToSegment = null)
        {
            if (_viewModel == null)
                return;

            try
            {
                // With multi-track layout, we need to find all segment ItemsControls within track rows
                if (TracksItemsControl == null)
                    return;

                // Iterate through each track item
                for (int trackIndex = 0; trackIndex < TracksItemsControl.Items.Count; trackIndex++)
                {
                    // Get the container for this track (which holds the entire track row Grid)
                    var trackContainer = TracksItemsControl.ItemContainerGenerator.ContainerFromIndex(trackIndex) as ContentPresenter;
                    if (trackContainer == null)
                        continue;

                    trackContainer.ApplyTemplate();

                    // Find the nested ItemsControl (SegmentsItemsControl) within this track row
                    var segmentsItemsControl = FindVisualChild<ItemsControl>(trackContainer);
                    if (segmentsItemsControl == null)
                        continue;

                    var track = TracksItemsControl.Items[trackIndex] as PodcastVideoEditor.Core.Models.Track;
                    if (track == null)
                        continue;
                    double trackHeight = TrackHeightConverter.GetHeight(track.TrackType);

                    // Update positioning for each segment in this track
                    for (int segmentIndex = 0; segmentIndex < segmentsItemsControl.Items.Count; segmentIndex++)
                    {
                        var presenter = segmentsItemsControl.ItemContainerGenerator.ContainerFromIndex(segmentIndex) as ContentPresenter;
                        if (presenter == null)
                            continue;

                        presenter.ApplyTemplate();

                        if (presenter.Content is not Segment segment)
                            continue;

                        if (limitToSegment != null && segment != limitToSegment)
                            continue;

                        var rootGrid = presenter.ContentTemplate?.FindName("SegmentRoot", presenter) as Grid
                            ?? FindVisualChild<System.Windows.Controls.Grid>(presenter);
                        if (rootGrid == null)
                            continue;

                        // Calculate position and size
                        double pixelX = _viewModel.TimeToPixels(segment.StartTime);
                        double pixelWidth = _viewModel.TimeToPixels(segment.EndTime - segment.StartTime);
                        pixelWidth = Math.Max(4, pixelWidth);

                        // Set Canvas positioning (relative to the parent Canvas)
                        Canvas.SetLeft(presenter, pixelX);
                        double segmentHeight = rootGrid.ActualHeight;
                        if (segmentHeight <= 0 || double.IsNaN(segmentHeight))
                            segmentHeight = rootGrid.Height;
                        if (segmentHeight <= 0 || double.IsNaN(segmentHeight))
                            segmentHeight = 40;
                        double verticalCenter = Math.Max(0, (trackHeight - segmentHeight) / 2);
                        Canvas.SetTop(presenter, verticalCenter);
                        rootGrid.Width = pixelWidth;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Visual tree may not be ready; will retry on next layout pass
            }
        }

        /// <summary>
        /// Update visual selection state of segments across all tracks.
        /// </summary>
        private void UpdateSegmentSelection()
        {
            if (_viewModel == null)
                return;

            try
            {
                if (TracksItemsControl == null)
                    return;

                // Iterate through each track item
                for (int trackIndex = 0; trackIndex < TracksItemsControl.Items.Count; trackIndex++)
                {
                    var trackContainer = TracksItemsControl.ItemContainerGenerator.ContainerFromIndex(trackIndex) as ContentPresenter;
                    if (trackContainer == null)
                        continue;

                    trackContainer.ApplyTemplate();

                    // Find the nested ItemsControl (SegmentsItemsControl) within this track row
                    var segmentsItemsControl = FindVisualChild<ItemsControl>(trackContainer);
                    if (segmentsItemsControl == null)
                        continue;

                    // Update selection state for each segment in this track
                    for (int segmentIndex = 0; segmentIndex < segmentsItemsControl.Items.Count; segmentIndex++)
                    {
                        var presenter = segmentsItemsControl.ItemContainerGenerator.ContainerFromIndex(segmentIndex) as ContentPresenter;
                        if (presenter == null)
                            continue;

                        presenter.ApplyTemplate();

                        if (presenter.Content is not Segment segment)
                            continue;

                        if (presenter.ContentTemplate == null)
                            continue;
                        var segmentFill = presenter.ContentTemplate.FindName("SegmentFill", presenter) as Border;
                        if (segmentFill == null)
                            continue;

                        bool isSelected = segment == _viewModel.SelectedSegment;
                        segmentFill.Background = isSelected ? SegmentSelectionBrushes.SelectedFill : SegmentSelectionBrushes.NormalFill;
                        segmentFill.BorderBrush = isSelected ? SegmentSelectionBrushes.SelectedBorder : SegmentSelectionBrushes.NormalBorder;
                        segmentFill.BorderThickness = new Thickness(2, 0, 2, 0);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Visual tree may not be ready
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Auto-scroll timeline so the segment stays visible when dragging (like CapCut).
        /// </summary>
        private void EnsureSegmentVisibleInScroll(Segment segment)
        {
            if (_viewModel == null || TimelineScroller == null)
                return;

            double startPx = _viewModel.TimeToPixels(segment.StartTime);
            double endPx = _viewModel.TimeToPixels(segment.EndTime);
            double viewportWidth = TimelineScroller.ViewportWidth;
            double offset = TimelineScroller.HorizontalOffset;
            const double margin = 50;

            double maxOffset = TimelineScroller.ScrollableWidth;
            if (double.IsNaN(maxOffset) || maxOffset < 0)
                maxOffset = 0;

            // Segment goes past right edge → scroll right
            if (endPx > offset + viewportWidth - margin)
            {
                double newOffset = Math.Min(endPx - viewportWidth + margin, maxOffset);
                TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, newOffset));
            }
            // Segment goes past left edge → scroll left
            else if (startPx < offset + margin)
            {
                double newOffset = Math.Max(0, startPx - margin);
                TimelineScroller.ScrollToHorizontalOffset(newOffset);
            }
        }

        /// <summary>
        /// Handle clicking on a segment (move thumb) to select it.
        /// </summary>
        private void MoveThumb_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                Focus();
                bool ctrlOrShift = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                if (ctrlOrShift)
                {
                    // Toggle this segment into/out of multi-selection
                    _viewModel?.ToggleMultiSelect(segment);
                }
                else
                {
                    // Normal single click: clear multi-selection and select only this segment
                    _viewModel?.ClearMultiSelection();
                    _viewModel?.SelectSegment(segment);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Start segment resize (right edge) operation.
        /// </summary>
        private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb && 
                thumb.Parent is Grid grid && 
                grid.DataContext is Segment segment)
            {
                // Block drag on locked track
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _isResizingLeft = false;
                _isResizingSegment = true;
                _segmentOriginalEndTime = segment.EndTime;
                _dragUndoOriginalEnd = segment.EndTime; // Stable snapshot for undo
                _resizeDeltaX = 0;
                grid.Cursor = Cursors.SizeWE;
                _viewModel!.IsDeferringThumbnailUpdate = true;
            }
        }

        /// <summary>
        /// Start segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                // Block drag on locked track
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _isResizingSegment = false;
                _isResizingLeft = true;
                _resizeLeftOriginalStartTime = segment.StartTime;
                _dragUndoOriginalStart = segment.StartTime; // Stable snapshot for undo
                _dragUndoOriginalSourceOffset = segment.SourceStartOffset; // Snapshot for undo
                _resizeLeftDeltaX = 0;
                grid.Cursor = Cursors.SizeWE;
                _viewModel!.IsDeferringThumbnailUpdate = true;
            }
        }

        /// <summary>
        /// Handle segment resize (right edge) drag.
        /// For audio segments, clamps to remaining source duration (SourceDuration - SourceStartOffset).
        /// </summary>
        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isResizingSegment || _viewModel == null)
                return;

            if (sender is Thumb thumb && 
                thumb.Parent is Grid grid && 
                grid.DataContext is Segment segment)
            {
                // Calculate new end time
                _resizeDeltaX += e.HorizontalChange;
                double pixelDelta = _resizeDeltaX;
                double timeDelta = _viewModel.PixelsToTime(pixelDelta);
                double newEndTime = _segmentOriginalEndTime + timeDelta;

                // Clamp to valid range
                if (newEndTime > _viewModel.TotalDuration)
                    newEndTime = _viewModel.TotalDuration;
                if (newEndTime <= segment.StartTime)
                    newEndTime = segment.StartTime + _viewModel.GridSize;

                // For audio segments: clamp to source file duration
                if (string.Equals(segment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    double? sourceDuration = _viewModel.GetSourceDurationForSegment(segment);
                    if (sourceDuration.HasValue && sourceDuration.Value > 0)
                    {
                        double maxSegDuration = sourceDuration.Value - segment.SourceStartOffset;
                        double maxEndTime = segment.StartTime + maxSegDuration;
                        if (newEndTime > maxEndTime)
                            newEndTime = maxEndTime;
                    }
                }

                // Magnetic snap to nearest segment edge
                double snapThresholdR = _viewModel.PixelsPerSecond > 0 ? 15.0 / _viewModel.PixelsPerSecond : 0.1;
                newEndTime = _viewModel.SnapToSegmentEdge(newEndTime, segment.TrackId, segment.Id, snapThresholdR);

                // Update segment via ViewModel
                bool updated = _viewModel.UpdateSegmentTiming(segment, segment.StartTime, newEndTime);

                // If snapped to boundary (e.g. hit next segment), reset baseline to prevent jitter
                const double tolerance = 0.01;
                if (updated && Math.Abs(segment.EndTime - newEndTime) > tolerance)
                {
                    _segmentOriginalEndTime = segment.EndTime;
                    _resizeDeltaX = 0;
                }

                if (updated)
                {
                    EnsureSegmentVisibleInScroll(segment);
                    UpdateSegmentLayout(segment);
                }
            }
        }

        /// <summary>
        /// Handle segment resize (left edge) drag — extend/shrink duration toward earlier time.
        /// For audio segments, also adjusts SourceStartOffset so the content trim tracks the edge.
        /// </summary>
        private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isResizingLeft || _viewModel == null)
                return;

            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                _resizeLeftDeltaX += e.HorizontalChange;
                double timeDelta = _viewModel.PixelsToTime(_resizeLeftDeltaX);
                double newStartTime = _resizeLeftOriginalStartTime + timeDelta;

                if (newStartTime < 0)
                    newStartTime = 0;
                if (newStartTime >= segment.EndTime)
                    newStartTime = segment.EndTime - _viewModel.GridSize;

                // For audio segments: clamp so SourceStartOffset cannot go negative
                bool isAudio = string.Equals(segment.Kind, "audio", StringComparison.OrdinalIgnoreCase);
                if (isAudio)
                {
                    double proposedOffsetDelta = newStartTime - _dragUndoOriginalStart;
                    double proposedOffset = _dragUndoOriginalSourceOffset + proposedOffsetDelta;
                    if (proposedOffset < 0)
                        newStartTime = _dragUndoOriginalStart - _dragUndoOriginalSourceOffset;
                }

                // Magnetic snap to nearest segment edge
                double snapThresholdL = _viewModel.PixelsPerSecond > 0 ? 15.0 / _viewModel.PixelsPerSecond : 0.1;
                newStartTime = _viewModel.SnapToSegmentEdge(newStartTime, segment.TrackId, segment.Id, snapThresholdL);

                bool updated = _viewModel.UpdateSegmentTiming(segment, newStartTime, segment.EndTime);

                // For audio segments: update SourceStartOffset to match the left-trim delta
                if (updated && isAudio)
                {
                    double offsetDelta = segment.StartTime - _dragUndoOriginalStart;
                    segment.SourceStartOffset = Math.Max(0, _dragUndoOriginalSourceOffset + offsetDelta);
                }

                const double tolerance = 0.01;
                if (updated && Math.Abs(segment.StartTime - newStartTime) > tolerance)
                {
                    _resizeLeftOriginalStartTime = segment.StartTime;
                    _resizeLeftDeltaX = 0;
                }

                if (updated)
                {
                    EnsureSegmentVisibleInScroll(segment);
                    UpdateSegmentLayout(segment);
                }
            }
        }

        /// <summary>
        /// End segment resize (right edge) operation.
        /// </summary>
        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isResizingSegment = false;
            _resizeDeltaX = 0;
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
            {
                grid.Cursor = Cursors.Arrow;
                if (grid.DataContext is Segment seg && Math.Abs(seg.EndTime - _dragUndoOriginalEnd) > 0.001)
                    _viewModel?.UndoRedoService?.Record(new SegmentTimingChangedAction(
                        seg, seg.StartTime, _dragUndoOriginalEnd, seg.StartTime, seg.EndTime,
                        () => _viewModel.InvalidateActiveSegmentsCachePublic()));
            }
            _viewModel!.IsDeferringThumbnailUpdate = false;
            UpdateSegmentLayout();
        }

        /// <summary>
        /// End segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isResizingLeft = false;
            _resizeLeftDeltaX = 0;
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
            {
                grid.Cursor = Cursors.Arrow;
                if (grid.DataContext is Segment seg && Math.Abs(seg.StartTime - _dragUndoOriginalStart) > 0.001)
                    _viewModel?.UndoRedoService?.Record(new SegmentTimingChangedAction(
                        seg, _dragUndoOriginalStart, seg.EndTime, seg.StartTime, seg.EndTime,
                        _dragUndoOriginalSourceOffset, seg.SourceStartOffset,
                        () => _viewModel.InvalidateActiveSegmentsCachePublic()));
            }
            _viewModel!.IsDeferringThumbnailUpdate = false;
            UpdateSegmentLayout();
        }

        /// <summary>
        /// Start segment move operation.
        /// </summary>
        private void MoveThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                // Block drag on locked track
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _isDraggingSegment = true;
                _segmentOriginalStartTime = segment.StartTime;
                _segmentOriginalEndTime = segment.EndTime;
                _dragUndoOriginalStart = segment.StartTime; // Stable snapshot for undo
                _dragUndoOriginalEnd = segment.EndTime;     // Stable snapshot for undo
                _moveDeltaX = 0;
                grid.Cursor = Cursors.SizeAll;
                _viewModel?.SelectSegment(segment);
                _viewModel!.IsDeferringThumbnailUpdate = true;
            }
        }

        /// <summary>
        /// Handle segment move drag.
        /// </summary>
        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isDraggingSegment || _viewModel == null)
                return;

            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                _moveDeltaX += e.HorizontalChange;
                double pixelDelta = _moveDeltaX;
                double timeDelta = _viewModel.PixelsToTime(pixelDelta);

                double duration = _segmentOriginalEndTime - _segmentOriginalStartTime;
                double newStart = _segmentOriginalStartTime + timeDelta;
                double newEnd = newStart + duration;

                // Clamp to valid range (keep duration)
                if (duration > _viewModel.TotalDuration)
                {
                    newStart = 0;
                    newEnd = _viewModel.TotalDuration;
                }
                else
                {
                    if (newStart < 0)
                    {
                        newStart = 0;
                        newEnd = duration;
                    }

                    if (newEnd > _viewModel.TotalDuration)
                    {
                        newEnd = _viewModel.TotalDuration;
                        newStart = _viewModel.TotalDuration - duration;
                    }
                }

                // Magnetic snap: check both edges, pick the closest snap while preserving duration
                double snapThresholdM = _viewModel.PixelsPerSecond > 0 ? 15.0 / _viewModel.PixelsPerSecond : 0.1;
                double snappedByStart = _viewModel.SnapToSegmentEdge(newStart, segment.TrackId, segment.Id, snapThresholdM);
                double snappedByEnd   = _viewModel.SnapToSegmentEdge(newEnd,   segment.TrackId, segment.Id, snapThresholdM);
                double distS = Math.Abs(snappedByStart - newStart);
                double distE = Math.Abs(snappedByEnd   - newEnd);
                if (distS <= distE && distS < snapThresholdM)
                {
                    newStart = snappedByStart;
                    newEnd   = newStart + duration;
                }
                else if (distE < snapThresholdM)
                {
                    newEnd   = snappedByEnd;
                    newStart = newEnd - duration;
                }

                bool updated = _viewModel.UpdateSegmentTiming(segment, newStart, newEnd);

                // If we snapped to boundary (applied position differs from requested), reset drag
                // origin to prevent oscillation when crossing segment boundaries
                const double tolerance = 0.01;
                if (updated && (Math.Abs(segment.StartTime - newStart) > tolerance || Math.Abs(segment.EndTime - newEnd) > tolerance))
                {
                    _segmentOriginalStartTime = segment.StartTime;
                    _segmentOriginalEndTime = segment.EndTime;
                    _moveDeltaX = 0;
                }

                if (updated)
                {
                    EnsureSegmentVisibleInScroll(segment);
                    UpdateSegmentLayout(segment);
                }
            }
        }

        /// <summary>
        /// End segment move operation.
        /// </summary>
        private void MoveThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDraggingSegment = false;
            _moveDeltaX = 0;
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
            {
                grid.Cursor = Cursors.Arrow;
                if (grid.DataContext is Segment seg &&
                    (Math.Abs(seg.StartTime - _dragUndoOriginalStart) > 0.001 || Math.Abs(seg.EndTime - _dragUndoOriginalEnd) > 0.001))
                    _viewModel?.UndoRedoService?.Record(new SegmentTimingChangedAction(
                        seg, _dragUndoOriginalStart, _dragUndoOriginalEnd, seg.StartTime, seg.EndTime,
                        () => _viewModel.InvalidateActiveSegmentsCachePublic()));
            }
            _viewModel!.IsDeferringThumbnailUpdate = false;
            UpdateSegmentLayout();
        }

        /// <summary>
        /// Right-click on a segment → show context menu with Split / Duplicate / Delete.
        /// </summary>
        private void MoveThumb_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null) return;

            if (sender is Thumb thumb && thumb.Parent is Grid grid && grid.DataContext is Segment segment)
            {
                _viewModel.SelectSegment(segment);

                var menu = new ContextMenu();

                var splitItem = new MenuItem
                {
                    Header = "Split at Playhead",
                    IsEnabled = _viewModel.SplitSelectedSegmentAtPlayheadCommand.CanExecute(null)
                };
                splitItem.Click += (_, _) => _viewModel.SplitSelectedSegmentAtPlayheadCommand.Execute(null);
                menu.Items.Add(splitItem);

                var dupItem = new MenuItem { Header = "Duplicate" };
                dupItem.Click += (_, _) => _viewModel.DuplicateSelectedSegmentCommand.Execute(null);
                menu.Items.Add(dupItem);

                menu.Items.Add(new Separator());

                var transitionItem = new MenuItem { Header = $"Transition… ({segment.TransitionType}, {segment.TransitionDuration:0.##}s)" };
                transitionItem.Click += (_, _) =>
                {
                    var dlg = new TransitionPickerDialog(segment.TransitionType, segment.TransitionDuration)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        segment.TransitionType     = dlg.SelectedTransition;
                        segment.TransitionDuration = dlg.SelectedDuration;
                        _viewModel.StatusMessage   = $"Transition set: {dlg.SelectedTransition} ({dlg.SelectedDuration:0.##}s)";
                        _viewModel.RequestProjectSave();
                    }
                };
                menu.Items.Add(transitionItem);

                menu.Items.Add(new Separator());

                var deleteItem = new MenuItem { Header = "Delete" };
                deleteItem.Click += (_, _) => _viewModel.DeleteSelectedSegmentCommand.Execute(null);
                menu.Items.Add(deleteItem);

                menu.PlacementTarget = thumb;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handle clicking on timeline to move playhead.
        /// Only seek when clicking on empty area (not on a segment block).
        /// </summary>
        private void TimelineCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnSegment(e))
                return; // Let segment handle selection

            Focus();
            _isDraggingPlayhead = true;
            var canvas = sender as IInputElement;
            HandlePlayheadPreview(canvas != null ? e.GetPosition(canvas).X : 0, force: true);
            e.Handled = true;
        }

        /// <summary>
        /// Handle dragging to move playhead.
        /// </summary>
        private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead && sender is IInputElement canvas)
            {
                HandlePlayheadPreview(e.GetPosition(canvas).X, force: false);
            }
        }

        /// <summary>
        /// Handle releasing playhead drag.
        /// </summary>
        private void TimelineCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPlayhead && _viewModel != null)
                _viewModel.CommitScrubSeek(_viewModel.PlayheadPosition);
            _isDraggingPlayhead = false;
        }

        /// <summary>
        /// Helper to move playhead and seek audio to clicked position.
        /// Must call SeekTo (not just set PlayheadPosition) else sync loop overwrites it.
        /// </summary>
        private void HandlePlayheadPreview(double pixelX, bool force)
        {
            if (_viewModel == null)
                return;

            if (!force)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastScrubPreviewTime).TotalMilliseconds < ScrubPreviewThrottleMs)
                    return;
                _lastScrubPreviewTime = now;
            }

            double newTime = _viewModel.PixelsToTime(Math.Max(0, pixelX));
            _viewModel.PreviewPlayhead(newTime);
        }

        /// <summary>
        /// Handle clicking on ruler to move playhead.
        /// </summary>
        private void RulerBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null || RulerControl == null)
                return;

            Focus();
            _isDraggingPlayhead = true;

            var position = e.GetPosition(RulerControl);
            double newTime = _viewModel.PixelsToTime(Math.Max(0, position.X));
            _viewModel.PreviewPlayhead(newTime);
            e.Handled = true;
        }

        /// <summary>
        /// Handle dragging on ruler to move playhead.
        /// </summary>
        private void RulerBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead && _viewModel != null && RulerControl != null)
            {
                HandlePlayheadPreview(e.GetPosition(RulerControl).X, force: false);
            }
        }

        /// <summary>
        /// Handle releasing drag on ruler.
        /// </summary>
        private void RulerBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPlayhead && _viewModel != null)
                _viewModel.CommitScrubSeek(_viewModel.PlayheadPosition);
            _isDraggingPlayhead = false;
        }

        /// <summary>
        /// Check if the click was on a segment block (vs empty timeline area).
        /// </summary>
        private static bool IsClickOnSegment(MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && fe.DataContext is Segment)
                    return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        // ─── Drag-and-Drop from Asset Panel ───

        private static readonly SolidColorBrush _dropHighlightBrush;
        private static readonly SolidColorBrush _dropInvalidBrush;
        private static readonly SolidColorBrush _trackNormalBg;

        static TimelineView()
        {
            _dropHighlightBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x43, 0xa0, 0x47)); // green tint
            _dropInvalidBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xe5, 0x39, 0x35));   // red tint
            _trackNormalBg = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d));
            _dropHighlightBrush.Freeze();
            _dropInvalidBrush.Freeze();
            _trackNormalBg.Freeze();
        }

        /// <summary>
        /// Validate drag-over on a track canvas. Shows visual feedback for compatible/incompatible drops.
        /// </summary>
        private void TrackCanvas_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (!e.Data.GetDataPresent("PVE_Asset"))
                return;

            if (sender is not Canvas canvas)
                return;

            var track = canvas.DataContext as PodcastVideoEditor.Core.Models.Track;
            var asset = e.Data.GetData("PVE_Asset") as Asset;

            if (track == null || asset == null)
                return;

            if (TimelineViewModel.IsAssetCompatibleWithTrack(asset, track))
            {
                e.Effects = DragDropEffects.Copy;
                canvas.Background = _dropHighlightBrush;
            }
            else
            {
                canvas.Background = _dropInvalidBrush;
            }

            e.Handled = true;
        }

        /// <summary>
        /// Reset visual highlight when drag leaves the track.
        /// </summary>
        private void TrackCanvas_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Canvas canvas)
                canvas.Background = _trackNormalBg;
        }

        /// <summary>
        /// Handle drop of an asset onto a track canvas. Creates a new segment at the drop position.
        /// </summary>
        private void TrackCanvas_Drop(object sender, DragEventArgs e)
        {
            if (sender is Canvas canvas)
                canvas.Background = _trackNormalBg;

            if (_viewModel == null)
                return;

            if (!e.Data.GetDataPresent("PVE_Asset"))
                return;

            var track = (sender as Canvas)?.DataContext as PodcastVideoEditor.Core.Models.Track;
            var asset = e.Data.GetData("PVE_Asset") as Asset;

            if (track == null || asset == null)
                return;

            if (!TimelineViewModel.IsAssetCompatibleWithTrack(asset, track))
                return;

            // Calculate drop time from pixel position
            var dropPoint = e.GetPosition(sender as IInputElement);
            double dropTimeSeconds = _viewModel.PixelsToTime(Math.Max(0, dropPoint.X));

            bool added = _viewModel.AddSegmentAtPositionOnTrack(track, dropTimeSeconds, asset);
            if (added)
            {
                UpdateSegmentLayout();
                UpdateSegmentSelection();
            }

            e.Handled = true;
        }

        /// <summary>
        /// Show context menu to add a new track of the selected type.
        /// </summary>
        private void AddTrack_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
                return;

            var menu = new System.Windows.Controls.ContextMenu();
            var addVisual = new System.Windows.Controls.MenuItem { Header = "Visual Track" };
            addVisual.Click += (_, _) => _viewModel.AddTrack(TrackTypes.Visual);
            var addText = new System.Windows.Controls.MenuItem { Header = "Text Track" };
            addText.Click += (_, _) => _viewModel.AddTrack(TrackTypes.Text);
            var addAudio = new System.Windows.Controls.MenuItem { Header = "Audio Track" };
            addAudio.Click += (_, _) => _viewModel.AddTrack(TrackTypes.Audio);

            menu.Items.Add(addVisual);
            menu.Items.Add(addText);
            menu.Items.Add(addAudio);

            if (sender is FrameworkElement btn)
            {
                menu.PlacementTarget = btn;
                menu.IsOpen = true;
            }
        }

        private void TrackLockButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                _viewModel?.ToggleTrackLock(track);
        }

        private void TrackVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                _viewModel?.ToggleTrackVisibility(track);
        }
    }
}
