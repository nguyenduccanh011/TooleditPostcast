using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Converters;
using PodcastVideoEditor.Ui.Helpers;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Generic;
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
        private SegmentDragOperation? _dragOp;
        private EventHandler? _undoRedoStateChangedHandler;
        private readonly DispatcherTimer _zoomTimer;
        private double _pendingZoomFactor = 1.0;
        private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;
        private NotifyCollectionChangedEventHandler? _tracksCollectionChangedHandler;
        private readonly List<(System.Collections.ObjectModel.ObservableCollection<Segment> collection, NotifyCollectionChangedEventHandler handler)> _segmentsCollectionHandlers = new();
        private DateTime _lastScrubPreviewTime = DateTime.MinValue;
        private const int ScrubPreviewThrottleMs = 16;
        private Point _trackHeaderDragStartPoint;
        private PodcastVideoEditor.Core.Models.Track? _draggedTrackHeader;

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
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.TotalDuration))
                    {
                        // Waveform no longer depends on TotalDuration (renders into ActualWidth).
                        // Only ruler and segment positions need updating.
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
                    }
                };
                _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

                // Subscribe to tracks collection changes — defer layout so new item container exists
                _tracksCollectionChangedHandler = (s, args) =>
                {
                    // When tracks are added/removed, update per-track Segments subscriptions
                    if (args.NewItems != null)
                    {
                        foreach (PodcastVideoEditor.Core.Models.Track newTrack in args.NewItems)
                            SubscribeToTrackSegments(newTrack);
                    }
                    if (args.OldItems != null)
                    {
                        foreach (PodcastVideoEditor.Core.Models.Track oldTrack in args.OldItems)
                            UnsubscribeFromTrackSegments(oldTrack);
                    }
                    if (args.Action == NotifyCollectionChangedAction.Reset)
                    {
                        UnsubscribeAllSegmentsHandlers();
                        foreach (PodcastVideoEditor.Core.Models.Track track in _viewModel.Tracks)
                            SubscribeToTrackSegments(track);
                    }
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                    {
                        if (IsLoaded)
                            UpdateSegmentLayout();
                    }));
                };
                _viewModel.Tracks.CollectionChanged += _tracksCollectionChangedHandler;

                // Subscribe to each existing track's Segments collection for segment add/remove
                foreach (PodcastVideoEditor.Core.Models.Track track in _viewModel.Tracks)
                    SubscribeToTrackSegments(track);

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
            UnsubscribeAllSegmentsHandlers();
            if (_viewModel?.UndoRedoService != null && _undoRedoStateChangedHandler != null)
                _viewModel.UndoRedoService.StateChanged -= _undoRedoStateChangedHandler;
            _zoomTimer.Stop();
        }

        /// <summary>
        /// Subscribe to a track's Segments collection to trigger deferred layout on segment add/remove.
        /// </summary>
        private void SubscribeToTrackSegments(PodcastVideoEditor.Core.Models.Track track)
        {
            if (track.Segments is System.Collections.ObjectModel.ObservableCollection<Segment> oc)
            {
                NotifyCollectionChangedEventHandler handler = (s, args) =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                    {
                        if (IsLoaded)
                        {
                            UpdateSegmentLayout();
                            UpdateSegmentSelection();
                        }
                    }));
                };
                oc.CollectionChanged += handler;
                _segmentsCollectionHandlers.Add((oc, handler));
            }
        }

        private void UnsubscribeFromTrackSegments(PodcastVideoEditor.Core.Models.Track track)
        {
            if (track.Segments is System.Collections.ObjectModel.ObservableCollection<Segment> oc)
            {
                for (int i = _segmentsCollectionHandlers.Count - 1; i >= 0; i--)
                {
                    if (_segmentsCollectionHandlers[i].collection == oc)
                    {
                        oc.CollectionChanged -= _segmentsCollectionHandlers[i].handler;
                        _segmentsCollectionHandlers.RemoveAt(i);
                    }
                }
            }
        }

        private void UnsubscribeAllSegmentsHandlers()
        {
            foreach (var (collection, handler) in _segmentsCollectionHandlers)
                collection.CollectionChanged -= handler;
            _segmentsCollectionHandlers.Clear();
        }

        /// <summary>
        /// Ctrl + mouse wheel: zoom timeline. Without Ctrl: vertical scroll via OuterScroller.
        /// NOTE: WPF ScrollViewer.OnMouseWheel always marks e.Handled=true even when
        /// VerticalScrollBarVisibility=Disabled, so TimelineScroller would eat the event and
        /// OuterScroller would never receive it. We must forward manually in PreviewMouseWheel.
        /// </summary>
        private void TimelineScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewModel == null)
                    return;
                double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                _pendingZoomFactor *= factor;
                _zoomTimer.Stop();
                _zoomTimer.Start();
                e.Handled = true;
            }
            else
            {
                // Forward vertical scroll to the outer scroller.
                // e.Delta is ±120 per notch; divide by 3 ≈ 40px per notch.
                OuterScroller.ScrollToVerticalOffset(OuterScroller.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
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

        private void ZoomSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null) return;
            // Reset zoom to fit the entire timeline in the visible area
            if (TimelineScroller.ActualWidth > 0)
                _viewModel.TimelineWidth = TimelineScroller.ActualWidth - 56;
            else
                _viewModel.TimelineWidth = 800;
        }

        private void InvalidateRuler()
        {
            RulerControl?.InvalidateVisual();
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
                            segmentHeight = TrackHeightConverter.GetHeight(track.TrackType);
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
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragOp = SegmentDragOperation.BeginResizeRight(segment, _viewModel!.PixelsPerSecond);
                grid.Cursor = Cursors.SizeWE;
                _viewModel.IsDeferringThumbnailUpdate = true;
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
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragOp = SegmentDragOperation.BeginResizeLeft(segment, _viewModel!.PixelsPerSecond);
                grid.Cursor = Cursors.SizeWE;
                _viewModel.IsDeferringThumbnailUpdate = true;
            }
        }

        /// <summary>
        /// Handle segment resize (right edge) drag.
        /// For audio segments, clamps to remaining source duration (SourceDuration - SourceStartOffset).
        /// </summary>
        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragOp?.Kind != DragKind.ResizeRight || _viewModel == null)
                return;

            var segment = _dragOp.Segment;
            double newEndTime = _dragOp.UpdateResizeRight(e.HorizontalChange, _viewModel.GridSize);

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
            newEndTime = _viewModel.SnapToSegmentEdge(newEndTime, segment.TrackId, segment.Id, _dragOp.SnapThreshold);

            bool updated = _viewModel.UpdateSegmentTiming(segment, segment.StartTime, newEndTime);

            if (updated)
            {
                _dragOp.HandleSnapCorrection(segment.StartTime, newEndTime);
                EnsureSegmentVisibleInScroll(segment);
                UpdateSegmentLayout(segment);
            }
        }

        /// <summary>
        /// Handle segment resize (left edge) drag — extend/shrink duration toward earlier time.
        /// For audio segments, also adjusts SourceStartOffset so the content trim tracks the edge.
        /// </summary>
        private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragOp?.Kind != DragKind.ResizeLeft || _viewModel == null)
                return;

            var segment = _dragOp.Segment;
            double newStartTime = _dragOp.UpdateResizeLeft(e.HorizontalChange, _viewModel.GridSize);

            // For audio segments: clamp so SourceStartOffset cannot go negative
            bool isAudio = string.Equals(segment.Kind, "audio", StringComparison.OrdinalIgnoreCase);
            if (isAudio)
                newStartTime = _dragOp.ClampForAudioLeft(newStartTime);

            // Magnetic snap to nearest segment edge
            newStartTime = _viewModel.SnapToSegmentEdge(newStartTime, segment.TrackId, segment.Id, _dragOp.SnapThreshold);

            bool updated = _viewModel.UpdateSegmentTiming(segment, newStartTime, segment.EndTime);

            // For audio segments: update SourceStartOffset to match the left-trim delta
            if (updated && isAudio)
                _dragOp.UpdateAudioSourceOffset();

            if (updated)
            {
                _dragOp.HandleSnapCorrection(newStartTime, segment.EndTime);
                EnsureSegmentVisibleInScroll(segment);
                UpdateSegmentLayout(segment);
            }
        }

        /// <summary>
        /// End segment resize (right edge) operation.
        /// </summary>
        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            if (_dragOp?.Kind == DragKind.ResizeRight)
            {
                var action = _dragOp.BuildUndoAction(() => _viewModel!.InvalidateActiveSegmentsCachePublic());
                if (action != null) _viewModel?.UndoRedoService?.Record(action);
            }
            _dragOp = null;
            _viewModel!.IsDeferringThumbnailUpdate = false;
            _viewModel.RecalculateDurationFromSegments();
            UpdateSegmentLayout();
        }

        /// <summary>
        /// End segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            if (_dragOp?.Kind == DragKind.ResizeLeft)
            {
                var action = _dragOp.BuildUndoAction(() => _viewModel!.InvalidateActiveSegmentsCachePublic());
                if (action != null) _viewModel?.UndoRedoService?.Record(action);
            }
            _dragOp = null;
            _viewModel!.IsDeferringThumbnailUpdate = false;
            _viewModel.RecalculateDurationFromSegments();
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
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragOp = SegmentDragOperation.BeginMove(segment, _viewModel!.PixelsPerSecond);
                grid.Cursor = Cursors.SizeAll;
                _viewModel.SelectSegment(segment);
                _viewModel.IsDeferringThumbnailUpdate = true;
            }
        }

        /// <summary>
        /// Handle segment move drag.
        /// </summary>
        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragOp?.Kind != DragKind.Move || _viewModel == null)
                return;

            var segment = _dragOp.Segment;
            var (newStart, newEnd) = _dragOp.UpdateMove(e.HorizontalChange);
            (newStart, newEnd) = _dragOp.ApplyMoveSnap(newStart, newEnd, _viewModel);

            bool updated = _viewModel.UpdateSegmentTiming(segment, newStart, newEnd);

            if (updated)
            {
                _dragOp.HandleSnapCorrection(newStart, newEnd);
                EnsureSegmentVisibleInScroll(segment);
                UpdateSegmentLayout(segment);
            }
        }

        /// <summary>
        /// End segment move operation.
        /// </summary>
        private void MoveThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            if (_dragOp?.Kind == DragKind.Move)
            {
                var action = _dragOp.BuildUndoAction(() => _viewModel!.InvalidateActiveSegmentsCachePublic());
                if (action != null) _viewModel?.UndoRedoService?.Record(action);
            }
            _dragOp = null;
            _viewModel!.IsDeferringThumbnailUpdate = false;
            _viewModel.RecalculateDurationFromSegments();
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
                    InputGestureText = "Ctrl+B",
                    IsEnabled = _viewModel.SplitSelectedSegmentAtPlayheadCommand.CanExecute(null)
                };
                splitItem.Click += (_, _) => _viewModel.SplitSelectedSegmentAtPlayheadCommand.Execute(null);
                menu.Items.Add(splitItem);

                var dupItem = new MenuItem { Header = "Duplicate", InputGestureText = "Ctrl+D" };
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

                var deleteItem = new MenuItem { Header = "Delete", InputGestureText = "Del" };
                deleteItem.Click += (_, _) => _viewModel.DeleteSelectedSegmentCommand.Execute(null);
                menu.Items.Add(deleteItem);

                menu.Items.Add(new Separator());

                var selectAllItem = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
                selectAllItem.Click += (_, _) => _viewModel.SelectAllSegmentsCommand.Execute(null);
                menu.Items.Add(selectAllItem);

                var clearAllItem = new MenuItem { Header = "Clear All" };
                clearAllItem.Click += (_, _) => _viewModel.ClearAllSegmentsCommand.Execute(null);
                menu.Items.Add(clearAllItem);

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
        /// Supports both asset drag (PVE_Asset) and element drag (PVE_ElementType).
        /// </summary>
        private void TrackCanvas_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (sender is not Canvas canvas)
                return;

            var track = canvas.DataContext as PodcastVideoEditor.Core.Models.Track;
            if (track == null)
                return;

            bool compatible = false;

            if (e.Data.GetDataPresent("PVE_Asset"))
            {
                var asset = e.Data.GetData("PVE_Asset") as Asset;
                if (asset != null)
                    compatible = TimelineViewModel.IsAssetCompatibleWithTrack(asset, track);
            }
            else if (e.Data.GetDataPresent("PVE_ElementType"))
            {
                var elementType = e.Data.GetData("PVE_ElementType") as string;
                if (!string.IsNullOrEmpty(elementType))
                    compatible = IsElementCompatibleWithTrack(elementType, track);
            }
            else
            {
                return;
            }

            if (compatible)
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
        /// Check if an element type tag is compatible with a track type.
        /// Text presets → text tracks, Visualizer → effect tracks.
        /// </summary>
        private static bool IsElementCompatibleWithTrack(string elementType, PodcastVideoEditor.Core.Models.Track track)
        {
            var trackType = track.TrackType?.ToLowerInvariant() ?? "";
            return elementType switch
            {
                "Visualizer" => trackType == TrackTypes.Effect,
                // All text presets (Title, Subtitle, LowerThird, Caption) → text tracks
                _ => trackType == TrackTypes.Text
            };
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
        /// Handle drop of an asset or element onto a track canvas.
        /// Asset drops create media segments; element drops create text/visualizer elements.
        /// </summary>
        private void TrackCanvas_Drop(object sender, DragEventArgs e)
        {
            if (sender is Canvas canvas)
                canvas.Background = _trackNormalBg;

            if (_viewModel == null)
                return;

            var track = (sender as Canvas)?.DataContext as PodcastVideoEditor.Core.Models.Track;
            if (track == null)
                return;

            // Calculate drop time from pixel position
            var dropPoint = e.GetPosition(sender as IInputElement);
            double dropTimeSeconds = _viewModel.PixelsToTime(Math.Max(0, dropPoint.X));

            bool added = false;

            if (e.Data.GetDataPresent("PVE_Asset"))
            {
                var asset = e.Data.GetData("PVE_Asset") as Asset;
                if (asset != null && TimelineViewModel.IsAssetCompatibleWithTrack(asset, track))
                    added = _viewModel.AddSegmentAtPositionOnTrack(track, dropTimeSeconds, asset);
            }
            else if (e.Data.GetDataPresent("PVE_ElementType"))
            {
                var elementType = e.Data.GetData("PVE_ElementType") as string;
                if (!string.IsNullOrEmpty(elementType) && IsElementCompatibleWithTrack(elementType, track))
                    added = HandleElementDrop(elementType, dropTimeSeconds, track.Id);
            }

            if (added)
            {
                UpdateSegmentLayout();
                UpdateSegmentSelection();
            }

            e.Handled = true;
        }

        /// <summary>
        /// Create a canvas element + timeline segment at the drop position.
        /// Routes through CanvasViewModel found via Window's MainViewModel.
        /// </summary>
        private bool HandleElementDrop(string elementType, double startTime, string trackId)
        {
            var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
            var canvasVm = mainVm?.CanvasViewModel;
            if (canvasVm == null)
                return false;

            if (elementType == "Visualizer")
            {
                canvasVm.AddVisualizerElementAt(startTime, trackId);
                return true;
            }

            if (System.Enum.TryParse<PodcastVideoEditor.Core.Models.TextStyle>(elementType, out var preset))
            {
                canvasVm.AddTextElementWithPreset(preset, startTime, trackId);
                return true;
            }

            return false;
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
            var addEffect = new System.Windows.Controls.MenuItem { Header = "Effect Track" };
            addEffect.Click += (_, _) => _viewModel.AddTrack(TrackTypes.Effect);

            menu.Items.Add(addVisual);
            menu.Items.Add(addText);
            menu.Items.Add(addAudio);
            menu.Items.Add(addEffect);

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

        /// <summary>
        /// Handle click on track header to select the track and display its properties.
        /// </summary>
        private void TrackHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null)
                return;

            // Don't intercept if the original click was on a Button (lock/visibility toggles)
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null)
            {
                if (dep is Button) return;
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (sender is Border border && border.DataContext is PodcastVideoEditor.Core.Models.Track track)
            {
                _viewModel.SelectTrack(track);
                _trackHeaderDragStartPoint = e.GetPosition(this);
                _draggedTrackHeader = track;
                e.Handled = true;
            }
        }

        private void TrackHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedTrackHeader == null)
                return;

            var current = e.GetPosition(this);
            var delta = current - _trackHeaderDragStartPoint;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            DragDrop.DoDragDrop((DependencyObject)sender, _draggedTrackHeader, DragDropEffects.Move);
            _draggedTrackHeader = null;
        }

        private void TrackHeader_DragOver(object sender, DragEventArgs e)
        {
            if (_viewModel == null || !e.Data.GetDataPresent(typeof(PodcastVideoEditor.Core.Models.Track)))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            if (sender is not Border border || border.DataContext is not PodcastVideoEditor.Core.Models.Track targetTrack)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            var draggedTrack = e.Data.GetData(typeof(PodcastVideoEditor.Core.Models.Track)) as PodcastVideoEditor.Core.Models.Track;
            if (draggedTrack == null || draggedTrack == targetTrack)
            {
                e.Effects = DragDropEffects.None;
                border.BorderBrush = Brushes.Transparent;
                return;
            }

            e.Effects = DragDropEffects.Move;
            var pos = e.GetPosition(border);
            border.BorderBrush = Brushes.DeepSkyBlue;
            border.BorderThickness = pos.Y < border.ActualHeight / 2
                ? new Thickness(0, 3, 1, 0)
                : new Thickness(0, 0, 1, 3);
            e.Handled = true;
        }

        private void TrackHeader_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4f));
                border.BorderThickness = new Thickness(0, 0, 1, 0);
            }
        }

        private void TrackHeader_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel == null || !e.Data.GetDataPresent(typeof(PodcastVideoEditor.Core.Models.Track)))
                return;

            if (sender is not Border border || border.DataContext is not PodcastVideoEditor.Core.Models.Track targetTrack)
                return;

            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4f));
            border.BorderThickness = new Thickness(0, 0, 1, 0);

            var draggedTrack = e.Data.GetData(typeof(PodcastVideoEditor.Core.Models.Track)) as PodcastVideoEditor.Core.Models.Track;
            if (draggedTrack == null || draggedTrack == targetTrack)
                return;

            var targetIndex = _viewModel.Tracks.IndexOf(targetTrack);
            if (targetIndex < 0)
                return;

            var pos = e.GetPosition(border);
            var insertIndex = pos.Y < border.ActualHeight / 2 ? targetIndex : targetIndex + 1;

            if (_viewModel.Tracks.IndexOf(draggedTrack) < targetIndex && pos.Y >= border.ActualHeight / 2)
                insertIndex--;

            _viewModel.ReorderTrack(draggedTrack, insertIndex);
            UpdateSegmentLayout();
            _draggedTrackHeader = null;
            e.Handled = true;
        }

        private void TrackMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                _viewModel?.MoveTrackUp(track);
        }

        private void TrackMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                _viewModel?.MoveTrackDown(track);
        }

        private void TrackRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                _viewModel?.RemoveTrack(track);
        }
    }
}
