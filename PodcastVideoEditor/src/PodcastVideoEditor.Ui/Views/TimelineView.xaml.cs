using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Converters;
using PodcastVideoEditor.Ui.Helpers;
using PodcastVideoEditor.Ui.Services;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
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
        private ISegmentDragHandler? _dragHandler;
        private DragAutoScrollHelper? _autoScrollHelper;
        private EventHandler? _undoRedoStateChangedHandler;
        private readonly DispatcherTimer _zoomTimer;
        private double _pendingZoomFactor = 1.0;
        private double _zoomMouseXInViewport;  // mouse X relative to TimelineScroller viewport
        private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;
        private NotifyCollectionChangedEventHandler? _tracksCollectionChangedHandler;
        private readonly List<(System.Collections.ObjectModel.ObservableCollection<Segment> collection, NotifyCollectionChangedEventHandler handler)> _segmentsCollectionHandlers = new();
        private DateTime _lastScrubPreviewTime = DateTime.MinValue;
        private const int ScrubPreviewThrottleMs = 16;
        private Point _trackHeaderDragStartPoint;
        private PodcastVideoEditor.Core.Models.Track? _draggedTrackHeader;

        // ── Multi-drag state ─────────────────────────────────────────────────
        private bool _isMultiDragging;
        private double _multiDragPrimaryOrigStart;
        private List<(Segment Seg, double OrigStart, double OrigEnd, double OrigSourceOffset)>? _multiDragCompanions;

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
                _dragHandler = new SegmentDragHandler(_viewModel);

                if (TimelineScroller != null)
                    TimelineScroller.PreviewMouseWheel += TimelineScroller_PreviewMouseWheel;

                // Shift+Z → zoom-to-fit (WPF KeyBinding doesn't support Shift+Letter cleanly)
                PreviewKeyDown += TimelineView_PreviewKeyDown;

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
            PreviewKeyDown -= TimelineView_PreviewKeyDown;
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
                // Remember mouse X relative to viewport for zoom-around-cursor
                _zoomMouseXInViewport = e.GetPosition(TimelineScroller).X;
                _zoomTimer.Stop();
                _zoomTimer.Start();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                // Shift + scroll → horizontal scroll (Premiere Pro / DaVinci Resolve convention)
                TimelineScroller.ScrollToHorizontalOffset(
                    TimelineScroller.HorizontalOffset - e.Delta / 3.0);
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

            // Zoom around cursor: remember the time position under the mouse,
            // apply zoom, then adjust scroll so that same time stays under cursor.
            double scrollOffset = TimelineScroller.HorizontalOffset;
            double mouseContentX = scrollOffset + _zoomMouseXInViewport;
            double timeAtCursor = _viewModel.PixelsToTime(mouseContentX);

            _viewModel.ZoomBy(_pendingZoomFactor);
            _pendingZoomFactor = 1.0;

            // After zoom, TimelineWidth & PixelsPerSecond have updated.
            double newContentX = _viewModel.TimeToPixels(timeAtCursor);
            double newOffset = newContentX - _zoomMouseXInViewport;
            TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, newOffset));
        }

        private void ZoomSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null) return;
            ZoomToFitViewport();
        }

        /// <summary>
        /// Zoom timeline to fit all content in the visible viewport. Called from
        /// zoom slider double-click and Shift+Z keyboard shortcut.
        /// </summary>
        /// <summary>Width of the track-header column (mirrors the XAML ColumnDefinition).</summary>
        private const double TrackHeaderWidth = 56;

        private void ZoomToFitViewport()
        {
            if (_viewModel == null) return;
            double available = TimelineScroller.ActualWidth > TrackHeaderWidth
                ? TimelineScroller.ActualWidth - TrackHeaderWidth
                : TimelineScroller.ActualWidth;
            _viewModel.TimelineWidth = Math.Max(TimelineConstants.MinTimelineWidth, available);
        }

        private void TimelineView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Shift+Z → zoom-to-fit (Premiere Pro shortcut)
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ZoomToFitViewport();
                e.Handled = true;
                return;
            }

            // Escape → clear multi-selection
            if (e.Key == Key.Escape && _viewModel != null)
            {
                _viewModel.ClearMultiSelection();
                _viewModel.StatusMessage = "Selection cleared";
                e.Handled = true;
                return;
            }

            // Arrow Left/Right → navigate to prev/next segment on same track
            if ((e.Key == Key.Left || e.Key == Key.Right) && _viewModel?.SelectedSegment != null && _viewModel.SelectedTrack != null)
            {
                var segments = _viewModel.SelectedTrack.Segments
                    .OrderBy(s => s.StartTime)
                    .ToList();
                int idx = segments.IndexOf(_viewModel.SelectedSegment);
                if (idx < 0) return;

                int newIdx = e.Key == Key.Left ? idx - 1 : idx + 1;
                if (newIdx >= 0 && newIdx < segments.Count)
                {
                    bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    if (shift)
                    {
                        // Shift+Arrow → extend selection
                        _viewModel.ToggleMultiSelect(segments[newIdx]);
                    }
                    else
                    {
                        _viewModel.ClearMultiSelection();
                        _viewModel.SelectSegment(segments[newIdx]);
                    }
                    EnsureSegmentVisibleInScroll(segments[newIdx]);
                }
                e.Handled = true;
            }
        }

        private void InvalidateRuler()
        {
            RulerControl?.InvalidateVisual();
        }

        /// <summary>
        /// Update playhead position based on ViewModel's PlayheadPosition.
        /// When playing or dragging, auto-scrolls the viewport to keep the playhead visible.
        /// </summary>
        private void UpdatePlayheadPosition()
        {
            if (_viewModel == null || PlayheadLine == null)
                return;

            double pixelX = _viewModel.TimeToPixels(_viewModel.PlayheadPosition);
            Canvas.SetLeft(PlayheadLine, pixelX);

            // Auto-scroll to follow playhead during playback (not during drag — drag has its own auto-scroll).
            if (_viewModel.IsPlaying && !_isDraggingPlayhead)
                AutoScrollToPlayhead(pixelX);
        }

        /// <summary>
        /// Scroll <see cref="TimelineScroller"/> so that <paramref name="pixelX"/> stays within the
        /// visible viewport. Called during playback auto-scroll AND during playhead drag.
        /// During drag the scroll is smooth/incremental so the user keeps visual feedback;
        /// during playback we page-jump so the UI doesn't continuously scroll.
        /// </summary>
        private void AutoScrollToPlayhead(double pixelX, bool isDrag = false)
        {
            if (TimelineScroller == null) return;

            double viewportWidth = TimelineScroller.ViewportWidth;
            double offset = TimelineScroller.HorizontalOffset;
            double rightMargin = isDrag ? Math.Max(16, viewportWidth * 0.03) : Math.Max(40, viewportWidth * 0.10);
            double leftMargin  = isDrag ? Math.Max(12, viewportWidth * 0.02) : Math.Max(20, viewportWidth * 0.05);

            if (pixelX > offset + viewportWidth - rightMargin)
            {
                if (isDrag)
                {
                    // Smooth incremental scroll: shift by the amount the playhead exceeds the margin
                    double overshoot = pixelX - (offset + viewportWidth - rightMargin);
                    TimelineScroller.ScrollToHorizontalOffset(offset + overshoot);
                }
                else
                {
                    // Playback: page-scroll so playhead is at left quarter
                    double newOffset = pixelX - viewportWidth * 0.25;
                    TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, newOffset));
                }
            }
            else if (pixelX < offset + leftMargin)
            {
                if (isDrag)
                {
                    double overshoot = (offset + leftMargin) - pixelX;
                    TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, offset - overshoot));
                }
                else
                {
                    double newOffset = pixelX - leftMargin;
                    TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, newOffset));
                }
            }
        }

        /// <summary>
        /// Show or hide the snap indicator line at the given timeline time.
        /// Pass null to hide the indicator.
        /// Uses hysteresis to prevent flicker: once shown, requires the snap to
        /// disappear for the indicator to hide, and small position jitter is absorbed.
        /// </summary>
        private double? _lastSnapIndicatorTime;
        private const double SnapIndicatorHysteresisPx = 3.0;

        private void UpdateSnapIndicator(double? snapTimeSeconds)
        {
            if (SnapIndicatorLine == null) return;

            if (snapTimeSeconds.HasValue && _viewModel != null)
            {
                double pixelX = snapTimeSeconds.Value * _viewModel.PixelsPerSecond;

                // Hysteresis: if indicator is already visible near this position, keep it steady
                if (_lastSnapIndicatorTime.HasValue)
                {
                    double lastPixelX = _lastSnapIndicatorTime.Value * _viewModel.PixelsPerSecond;
                    if (Math.Abs(pixelX - lastPixelX) < SnapIndicatorHysteresisPx)
                        return; // Absorb small jitter
                }

                Canvas.SetLeft(SnapIndicatorLine, pixelX);
                SnapIndicatorLine.Visibility = Visibility.Visible;
                _lastSnapIndicatorTime = snapTimeSeconds.Value;
            }
            else
            {
                SnapIndicatorLine.Visibility = Visibility.Collapsed;
                _lastSnapIndicatorTime = null;
            }
        }

        // ─── Drop visual helpers (guide line, ghost preview) ───

        private static readonly SolidColorBrush _dropGhostNormalBg;
        private static readonly SolidColorBrush _dropGhostNormalBorder;
        private static readonly SolidColorBrush _dropGhostCollisionBg;
        private static readonly SolidColorBrush _dropGhostCollisionBorder;

        /// <summary>
        /// Show the vertical drop guide line at the given pixel X position.
        /// </summary>
        private void UpdateDropGuideLine(double pixelX)
        {
            if (DropGuideLine == null) return;
            Canvas.SetLeft(DropGuideLine, pixelX);
            DropGuideLine.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Show the ghost preview rectangle at the drop position.
        /// Changes color when a collision is detected (segment will be placed on a new auto-created track).
        /// </summary>
        private void UpdateDropGhostPreview(double pixelX, double durationSeconds, string label, bool wouldCollide)
        {
            if (DropGhostPreview == null || _viewModel == null) return;

            double widthPx = _viewModel.TimeToPixels(durationSeconds);
            DropGhostPreview.Width = Math.Max(20, widthPx);
            Canvas.SetLeft(DropGhostPreview, pixelX);
            Canvas.SetTop(DropGhostPreview, 4);
            DropGhostPreview.Visibility = Visibility.Visible;

            if (wouldCollide)
            {
                DropGhostPreview.Background = _dropGhostCollisionBg;
                DropGhostPreview.BorderBrush = _dropGhostCollisionBorder;
                if (DropGhostLabel != null)
                    DropGhostLabel.Text = $"+ {label}";
            }
            else
            {
                DropGhostPreview.Background = _dropGhostNormalBg;
                DropGhostPreview.BorderBrush = _dropGhostNormalBorder;
                if (DropGhostLabel != null)
                    DropGhostLabel.Text = label;
            }
        }

        /// <summary>
        /// Hide all drop visual indicators (guide line + ghost preview).
        /// </summary>
        private void HideDropVisuals()
        {
            if (DropGuideLine != null)
                DropGuideLine.Visibility = Visibility.Collapsed;
            if (DropGhostPreview != null)
                DropGhostPreview.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Update segment vertical centering within tracks.
        /// Horizontal position (Canvas.Left) and width are handled by data binding
        /// to Segment.PixelLeft and Segment.PixelWidth, so this method only sets Canvas.Top.
        /// </summary>
        private void UpdateSegmentLayout(Segment? limitToSegment = null)
        {
            if (_viewModel == null)
                return;

            try
            {
                if (TracksItemsControl == null)
                    return;

                for (int trackIndex = 0; trackIndex < TracksItemsControl.Items.Count; trackIndex++)
                {
                    var trackContainer = TracksItemsControl.ItemContainerGenerator.ContainerFromIndex(trackIndex) as ContentPresenter;
                    if (trackContainer == null)
                        continue;

                    trackContainer.ApplyTemplate();

                    var segmentsItemsControl = FindVisualChild<ItemsControl>(trackContainer);
                    if (segmentsItemsControl == null)
                        continue;

                    var track = TracksItemsControl.Items[trackIndex] as PodcastVideoEditor.Core.Models.Track;
                    if (track == null)
                        continue;
                    double trackHeight = TrackHeightConverter.GetHeight(track.TrackType);

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

                        // Vertical centering only — horizontal position and width are binding-driven
                        double segmentHeight = rootGrid.ActualHeight;
                        if (segmentHeight <= 0 || double.IsNaN(segmentHeight))
                            segmentHeight = rootGrid.Height;
                        if (segmentHeight <= 0 || double.IsNaN(segmentHeight))
                            segmentHeight = TrackHeightConverter.GetHeight(track.TrackType);
                        double verticalCenter = Math.Max(0, (trackHeight - segmentHeight) / 2);
                        Canvas.SetTop(presenter, verticalCenter);
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
        /// Determine which track the mouse cursor is currently over by hit-testing
        /// the TracksItemsControl. Returns the Track data context of the row under the mouse,
        /// or null if no track is found.
        /// </summary>
        private PodcastVideoEditor.Core.Models.Track? GetTrackUnderMouse()
        {
            if (TracksItemsControl == null) return null;

            var mousePos = Mouse.GetPosition(TracksItemsControl);
            var hitResult = VisualTreeHelper.HitTest(TracksItemsControl, mousePos);
            if (hitResult?.VisualHit == null) return null;

            // Walk up the visual tree from the hit element to find a ContentPresenter
            // whose content is a Track
            DependencyObject? current = hitResult.VisualHit;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is PodcastVideoEditor.Core.Models.Track track)
                    return track;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

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
        /// Ctrl+Click = toggle, Shift+Click = range select, Click = single select.
        /// </summary>
        private void MoveThumb_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                Focus();
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                if (ctrl)
                {
                    // Toggle this segment into/out of multi-selection
                    _viewModel?.ToggleMultiSelect(segment);
                }
                else if (shift)
                {
                    // Range-select from anchor to this segment
                    _viewModel?.RangeMultiSelect(segment);
                }
                else
                {
                    // Normal single click: clear multi-selection and select only this segment
                    _viewModel?.ClearMultiSelection();
                    _viewModel?.SelectSegment(segment);

                    // Click-on-segment should also move playhead to the exact clicked time.
                    // Without this, playback may resume from stale/segment boundary positions.
                    if (_viewModel != null)
                    {
                        var localX = e.GetPosition(grid).X;
                        var clickedTime = segment.StartTime + _viewModel.PixelsToTime(Math.Max(0, localX));
                        clickedTime = Math.Clamp(clickedTime, segment.StartTime, segment.EndTime);
                        _viewModel.SeekTo(clickedTime);
                    }
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Start segment resize (right edge) operation.
        /// </summary>
        private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_viewModel == null)
            {
                e.Handled = true;
                return;
            }

            if (sender is Thumb thumb && 
                thumb.Parent is Grid grid && 
                grid.DataContext is Segment segment)
            {
                var track = _viewModel.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragHandler?.BeginResizeRight(segment, _viewModel.PixelsPerSecond);
                grid.Cursor = Cursors.SizeWE;

                _autoScrollHelper ??= new DragAutoScrollHelper();
                _autoScrollHelper.Start(
                    TimelineScroller,
                    () => Mouse.GetPosition(TimelineScroller),
                    endTime => _viewModel.ExpandTimelineToFit(endTime > 0 ? endTime : _viewModel.TotalDuration + 2));
            }
        }

        /// <summary>
        /// Start segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_viewModel == null)
            {
                e.Handled = true;
                return;
            }

            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                var track = _viewModel.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragHandler?.BeginResizeLeft(segment, _viewModel.PixelsPerSecond);
                grid.Cursor = Cursors.SizeWE;

                _autoScrollHelper ??= new DragAutoScrollHelper();
                _autoScrollHelper.Start(
                    TimelineScroller,
                    () => Mouse.GetPosition(TimelineScroller),
                    endTime => _viewModel.ExpandTimelineToFit(endTime > 0 ? endTime : _viewModel.TotalDuration + 2));
            }
        }

        /// <summary>
        /// Handle segment resize (right edge) drag.
        /// All processing logic is delegated to the ISegmentDragHandler.
        /// </summary>
        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragHandler?.ActiveDrag?.Kind != DragKind.ResizeRight)
                return;

            var result = _dragHandler.ProcessDelta(e.HorizontalChange);
            UpdateSnapIndicator(result.SnapIndicatorTime);
            if (result.Updated)
            {
                EnsureSegmentVisibleInScroll(result.Segment);
                UpdateSegmentLayout(result.Segment);
            }
        }

        /// <summary>
        /// Handle segment resize (left edge) drag.
        /// All processing logic is delegated to the ISegmentDragHandler.
        /// </summary>
        private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragHandler?.ActiveDrag?.Kind != DragKind.ResizeLeft)
                return;

            var result = _dragHandler.ProcessDelta(e.HorizontalChange);
            UpdateSnapIndicator(result.SnapIndicatorTime);
            if (result.Updated)
            {
                EnsureSegmentVisibleInScroll(result.Segment);
                UpdateSegmentLayout(result.Segment);
            }
        }

        /// <summary>
        /// End segment resize (right edge) operation.
        /// </summary>
        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            _autoScrollHelper?.Stop();
            UpdateSnapIndicator(null);

            var action = _dragHandler?.CompleteDrag();
            if (action != null) _viewModel?.UndoRedoService?.Record(action);
            UpdateSegmentLayout();
        }

        /// <summary>
        /// End segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            _autoScrollHelper?.Stop();
            UpdateSnapIndicator(null);

            var action = _dragHandler?.CompleteDrag();
            if (action != null) _viewModel?.UndoRedoService?.Record(action);
            UpdateSegmentLayout();
        }

        /// <summary>
        /// Start segment move operation.
        /// If the dragged segment is part of a multi-selection, all selected segments
        /// move together (multi-drag). Otherwise single-drag as before.
        /// </summary>
        private void MoveThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.DataContext is Segment segment)
            {
                var track = _viewModel?.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track?.IsLocked == true) { e.Handled = true; return; }

                _dragHandler?.BeginMove(segment, _viewModel!.PixelsPerSecond);
                grid.Cursor = Cursors.SizeAll;

                // Multi-drag: if dragged segment is already multi-selected, move all together
                var selSvc = _viewModel!.SelectionService;
                if (selSvc.IsSelected(segment) && selSvc.Count > 1)
                {
                    _isMultiDragging = true;
                    _multiDragPrimaryOrigStart = segment.StartTime;
                    _multiDragCompanions = new List<(Segment, double, double, double)>();
                    foreach (var sel in selSvc.SelectedSegments)
                    {
                        if (sel == segment) continue;
                        var selTrack = _viewModel.Tracks.FirstOrDefault(t => t.Id == sel.TrackId);
                        if (selTrack?.IsLocked == true) continue;
                        _multiDragCompanions.Add((sel, sel.StartTime, sel.EndTime, sel.SourceStartOffset));
                        sel.IsDragging = true;
                    }
                    // Don't call SelectSegment — preserve multi-selection
                }
                else
                {
                    _isMultiDragging = false;
                    _multiDragCompanions = null;
                    _viewModel!.SelectSegment(segment);
                }

                _autoScrollHelper ??= new DragAutoScrollHelper();
                _autoScrollHelper.Start(
                    TimelineScroller,
                    () => Mouse.GetPosition(TimelineScroller),
                    endTime => _viewModel.ExpandTimelineToFit(endTime > 0 ? endTime : _viewModel.TotalDuration + 2));
            }
        }

        /// <summary>
        /// Handle segment move drag — delegated to ISegmentDragHandler.
        /// Includes cross-track detection: if the mouse moves into a compatible track,
        /// the segment is reassigned to that track during the drag.
        /// Multi-drag: companion segments follow the same horizontal delta.
        /// </summary>
        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_dragHandler?.ActiveDrag?.Kind != DragKind.Move)
                return;

            // Cross-track detection (primary segment only — companions stay on their tracks)
            if (_dragHandler.AccumulateVerticalDelta(e.VerticalChange) && TracksItemsControl != null && _viewModel != null)
            {
                if (!_isMultiDragging)
                {
                    var targetTrack = GetTrackUnderMouse();
                    if (targetTrack != null)
                        _dragHandler.TryMoveToTrack(targetTrack);
                }
            }

            bool isRipple = !_isMultiDragging && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var result = _dragHandler.ProcessDelta(e.HorizontalChange, isRipple);
            UpdateSnapIndicator(result.SnapIndicatorTime);

            // Move companion segments by the same delta
            if (_isMultiDragging && _multiDragCompanions != null && result.Updated && result.Segment != null)
            {
                double delta = result.Segment.StartTime - _multiDragPrimaryOrigStart;
                foreach (var (seg, origStart, origEnd, _) in _multiDragCompanions)
                {
                    double newStart = Math.Max(0, origStart + delta);
                    double newEnd = origEnd + delta;
                    if (newEnd > newStart)
                    {
                        seg.StartTime = newStart;
                        seg.EndTime = newEnd;
                    }
                }
            }

            if (result.Updated)
                UpdateSegmentLayout(isRipple ? null : (_isMultiDragging ? null : result.Segment));
        }

        /// <summary>
        /// End segment move operation.
        /// Multi-drag: builds a CompoundAction covering primary + all companions.
        /// </summary>
        private void MoveThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;

            _autoScrollHelper?.Stop();
            UpdateSnapIndicator(null);

            var primaryAction = _dragHandler?.CompleteDrag();

            if (_isMultiDragging && _multiDragCompanions != null && _viewModel != null)
            {
                var allActions = new List<IUndoableAction>();
                if (primaryAction != null)
                    allActions.Add(primaryAction);

                foreach (var (seg, origStart, origEnd, origSourceOffset) in _multiDragCompanions)
                {
                    seg.IsDragging = false;
                    // Only record undo if the companion actually moved
                    if (Math.Abs(seg.StartTime - origStart) > 0.001 || Math.Abs(seg.EndTime - origEnd) > 0.001)
                    {
                        allActions.Add(new SegmentTimingChangedAction(
                            seg, origStart, origEnd,
                            seg.StartTime, seg.EndTime,
                            origSourceOffset, seg.SourceStartOffset,
                            () => _viewModel.InvalidateActiveSegmentsCachePublic()));
                    }
                }

                if (allActions.Count > 0)
                    _viewModel.UndoRedoService?.Record(
                        allActions.Count == 1 ? allActions[0] : new CompoundAction("Move segments", allActions));

                _isMultiDragging = false;
                _multiDragCompanions = null;
            }
            else
            {
                if (primaryAction != null) _viewModel?.UndoRedoService?.Record(primaryAction);
            }

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

                // 🖼️ Image replacement menu items (only for visual segments with background)
                if (_viewModel != null)
                {
                    var hasBackground = _viewModel.SelectedSegmentHasBackground;
                    var hasBackgroundAssetId = !string.IsNullOrEmpty(segment.BackgroundAssetId);

                    var replaceImageItem = new MenuItem
                    {
                        Header = "🖼️ Replace Image",
                        IsEnabled = hasBackground
                    };
                    replaceImageItem.Click += (_, _) => _viewModel.ReplaceSegmentImageCommand.Execute(null);
                    menu.Items.Add(replaceImageItem);

                    var clearImageItem = new MenuItem
                    {
                        Header = "❌ Clear Image",
                        IsEnabled = hasBackground && hasBackgroundAssetId
                    };
                    clearImageItem.Click += (_, _) => _viewModel.ClearSegmentBackgroundCommand.Execute(null);
                    menu.Items.Add(clearImageItem);
                }

                menu.Items.Add(new Separator());

                var deleteItem = new MenuItem { Header = "Delete", InputGestureText = "Del" };
                deleteItem.Click += (_, _) => _viewModel.DeleteSelectedSegmentCommand.Execute(null);
                menu.Items.Add(deleteItem);

                menu.Items.Add(new Separator());

                var selectAllItem = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
                selectAllItem.Click += (_, _) => _viewModel.SelectAllSegmentsCommand.Execute(null);
                menu.Items.Add(selectAllItem);

                var closeGapsItem = new MenuItem { Header = "Close Gaps", InputGestureText = "Ctrl+G" };
                closeGapsItem.Click += (_, _) => _viewModel.CloseGapsOnTrackCommand.Execute(null);
                menu.Items.Add(closeGapsItem);

                var clearAllItem = new MenuItem { Header = "Clear All" };
                clearAllItem.Click += (_, _) => _viewModel.ClearAllSegmentsCommand.Execute(null);
                menu.Items.Add(clearAllItem);

                menu.PlacementTarget = thumb;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        // ── Rubber-band selection state ──────────────────────────────────────
        private bool _isRubberBanding;
        private Point _rubberBandOrigin;
        private Canvas? _rubberBandCanvas;          // parent canvas where rubber-band started
        private System.Windows.Shapes.Rectangle? _rubberBandRect;

        /// <summary>
        /// Handle clicking on timeline empty area: clear multi-selection + start rubber-band or playhead drag.
        /// </summary>
        private void TimelineCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnSegment(e))
                return; // Let segment handle selection

            Focus();

            // Clear multi-selection when clicking empty area (unless Ctrl held for additive)
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!ctrl)
                _viewModel?.ClearMultiSelection();

            if (sender is Canvas canvas && e.LeftButton == MouseButtonState.Pressed)
            {
                _rubberBandOrigin = e.GetPosition(canvas);
                _rubberBandCanvas = canvas;
                _isRubberBanding = false; // Will activate on move if drag exceeds threshold
                canvas.CaptureMouse();

                // Also start playhead preview immediately
                HandlePlayheadPreview(_rubberBandOrigin.X, force: true);
                _isDraggingPlayhead = true;
            }
            e.Handled = true;
        }

        /// <summary>
        /// Handle dragging on empty area: switch to rubber-band if moved enough, otherwise continue playhead drag.
        /// </summary>
        private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Canvas canvas) return;

            if (e.LeftButton == MouseButtonState.Pressed && _rubberBandCanvas == canvas)
            {
                var pos = e.GetPosition(canvas);
                double dx = Math.Abs(pos.X - _rubberBandOrigin.X);
                double dy = Math.Abs(pos.Y - _rubberBandOrigin.Y);

                // Activate rubber-band if moved > 5px from origin
                if (!_isRubberBanding && (dx > 5 || dy > 5))
                {
                    _isRubberBanding = true;
                    _isDraggingPlayhead = false; // Switch from playhead to rubber-band

                    // Create selection rectangle overlay
                    _rubberBandRect = new System.Windows.Shapes.Rectangle
                    {
                        Stroke = new SolidColorBrush(Color.FromArgb(200, 0x64, 0xb5, 0xf6)),
                        StrokeThickness = 1,
                        Fill = new SolidColorBrush(Color.FromArgb(40, 0x64, 0xb5, 0xf6)),
                        StrokeDashArray = new DoubleCollection { 4, 2 },
                        IsHitTestVisible = false
                    };
                    canvas.Children.Add(_rubberBandRect);
                }

                if (_isRubberBanding && _rubberBandRect != null)
                {
                    double x = Math.Min(pos.X, _rubberBandOrigin.X);
                    double y = Math.Min(pos.Y, _rubberBandOrigin.Y);
                    double w = Math.Abs(pos.X - _rubberBandOrigin.X);
                    double h = Math.Abs(pos.Y - _rubberBandOrigin.Y);

                    Canvas.SetLeft(_rubberBandRect, x);
                    Canvas.SetTop(_rubberBandRect, y);
                    _rubberBandRect.Width = w;
                    _rubberBandRect.Height = h;
                }
                else if (_isDraggingPlayhead)
                {
                    HandlePlayheadPreview(pos.X, force: false);
                }
            }
        }

        /// <summary>
        /// Handle releasing: complete rubber-band selection or playhead drag.
        /// </summary>
        private void TimelineCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas canvas)
            {
                if (_isRubberBanding && _viewModel != null)
                {
                    var pos = e.GetPosition(canvas);

                    // Calculate time range from pixel positions
                    double x1 = Math.Min(pos.X, _rubberBandOrigin.X);
                    double x2 = Math.Max(pos.X, _rubberBandOrigin.X);
                    double startTime = _viewModel.PixelsToTime(x1);
                    double endTime = _viewModel.PixelsToTime(x2);

                    // Determine which track this canvas belongs to
                    var hitTracks = GetRubberBandHitTracks(canvas);

                    bool additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                    _viewModel.SelectionService.RubberBandSelect(startTime, endTime, hitTracks, additive);
                    _viewModel.StatusMessage = _viewModel.SelectionService.HasSelection
                        ? $"{_viewModel.SelectionService.Count} segment(s) selected"
                        : "No segments in selection area";

                    // Remove the rectangle visual
                    if (_rubberBandRect != null)
                    {
                        canvas.Children.Remove(_rubberBandRect);
                        _rubberBandRect = null;
                    }
                }
                else if (_isDraggingPlayhead && _viewModel != null)
                {
                    _viewModel.CommitScrubSeek(_viewModel.PlayheadPosition);
                }

                canvas.ReleaseMouseCapture();
            }
            _isDraggingPlayhead = false;
            _isRubberBanding = false;
            _rubberBandCanvas = null;
        }

        /// <summary>
        /// Get the track(s) under the rubber-band canvas.
        /// Since each track has its own Canvas, rubber-band within one track returns only that track.
        /// </summary>
        private IReadOnlyList<PodcastVideoEditor.Core.Models.Track> GetRubberBandHitTracks(Canvas canvas)
        {
            // The canvas is inside a DataTemplate whose DataContext is a Track
            if (canvas.DataContext is PodcastVideoEditor.Core.Models.Track track)
                return new[] { track };

            // Fallback: return all tracks
            return _viewModel?.Tracks as IReadOnlyList<PodcastVideoEditor.Core.Models.Track> ?? Array.Empty<PodcastVideoEditor.Core.Models.Track>();
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

            double clampedX = Math.Max(0, pixelX);
            double newTime = _viewModel.PixelsToTime(clampedX);
            _viewModel.PreviewPlayhead(newTime);

            // Auto-scroll the viewport so the playhead stays visible while dragging
            if (_isDraggingPlayhead)
                AutoScrollToPlayhead(_viewModel.TimeToPixels(newTime), isDrag: true);
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
            if (sender is IInputElement inputElement)
                inputElement.CaptureMouse();

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
            if (_isDraggingPlayhead)
            {
                if (sender is IInputElement inputElement)
                    inputElement.ReleaseMouseCapture();
                if (_viewModel != null)
                    _viewModel.CommitScrubSeek(_viewModel.PlayheadPosition);
            }
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

            // Ghost preview brushes
            _dropGhostNormalBg = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xBC, 0xD4));       // cyan tint
            _dropGhostNormalBorder = new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4));           // cyan
            _dropGhostCollisionBg = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x98, 0x00));     // orange tint
            _dropGhostCollisionBorder = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));        // orange
            _dropGhostNormalBg.Freeze();
            _dropGhostNormalBorder.Freeze();
            _dropGhostCollisionBg.Freeze();
            _dropGhostCollisionBorder.Freeze();
        }

        /// <summary>
        /// Validate drag-over on a track canvas. Shows visual feedback for compatible/incompatible drops.
        /// Supports both asset drag (PVE_Asset) and element drag (PVE_ElementType).
        /// Shows a vertical drop guide line and ghost preview at the drop position.
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
            double dropDuration = 5.0; // default preview duration
            string dropLabel = "";

            if (e.Data.GetDataPresent("PVE_Asset"))
            {
                var asset = e.Data.GetData("PVE_Asset") as Asset;
                if (asset != null)
                {
                    compatible = TimelineViewModel.IsAssetCompatibleWithTrack(asset, track);
                    dropDuration = asset.Duration > 0 ? asset.Duration.Value : 5.0;
                    dropLabel = asset.Name ?? "Asset";
                }
            }
            else if (e.Data.GetDataPresent("PVE_GlobalAsset"))
            {
                // Global library assets are always images → compatible with visual tracks
                var trackType = track.TrackType?.ToLowerInvariant() ?? "";
                compatible = trackType == TrackTypes.Visual;
                dropLabel = "Image";
            }
            else if (e.Data.GetDataPresent("PVE_ElementType"))
            {
                var elementType = e.Data.GetData("PVE_ElementType") as string;
                if (!string.IsNullOrEmpty(elementType))
                {
                    compatible = IsElementCompatibleWithTrack(elementType, track);
                    dropLabel = elementType;
                }
            }
            else
            {
                return;
            }

            if (compatible)
            {
                e.Effects = DragDropEffects.Copy;
                canvas.Background = _dropHighlightBrush;

                // Show drop guide line and ghost preview
                if (_viewModel != null)
                {
                    var dropPoint = e.GetPosition(canvas);
                    double dropTimeSec = _viewModel.PixelsToTime(Math.Max(0, dropPoint.X));
                    double pixelX = _viewModel.TimeToPixels(dropTimeSec);

                    UpdateDropGuideLine(pixelX);

                    bool wouldCollide = _viewModel.WouldCollideOnDrop(track, dropTimeSec, dropDuration);
                    UpdateDropGhostPreview(pixelX, dropDuration, dropLabel, wouldCollide);
                }
            }
            else
            {
                canvas.Background = _dropInvalidBrush;
                HideDropVisuals();
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
            HideDropVisuals();
        }

        /// <summary>
        /// Handle drop of an asset or element onto a track canvas.
        /// Asset drops create media segments; element drops create text/visualizer elements.
        /// When a collision is detected, a new track is auto-created above the target.
        /// </summary>
        private void TrackCanvas_Drop(object sender, DragEventArgs e)
        {
            if (sender is Canvas canvas)
                canvas.Background = _trackNormalBg;

            HideDropVisuals();

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
                    added = _viewModel.AddSegmentWithAutoTrack(track, dropTimeSeconds, asset) != null;
            }
            else if (e.Data.GetDataPresent("PVE_GlobalAsset"))
            {
                var globalAsset = e.Data.GetData("PVE_GlobalAsset") as PodcastVideoEditor.Core.Models.GlobalAsset;
                if (globalAsset != null)
                {
                    _ = HandleGlobalAssetDropAsync(globalAsset, track, dropTimeSeconds);
                    added = true; // optimistic — the async method handles errors
                }
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
        /// Handle drop of a global library asset onto a timeline track.
        /// Copies the asset into the current project (copy-on-use), then creates a segment.
        /// </summary>
        private async System.Threading.Tasks.Task HandleGlobalAssetDropAsync(
            PodcastVideoEditor.Core.Models.GlobalAsset globalAsset,
            PodcastVideoEditor.Core.Models.Track track,
            double dropTimeSeconds)
        {
            var mainVm = Window.GetWindow(this)?.DataContext as MainViewModel;
            var projectVm = mainVm?.ProjectViewModel;
            if (projectVm?.CurrentProject == null || _viewModel == null)
                return;

            if (!System.IO.File.Exists(globalAsset.FilePath))
            {
                Serilog.Log.Warning("Global asset file not found: {Path}", globalAsset.FilePath);
                return;
            }

            try
            {
                // Check if already imported into this project
                var existing = projectVm.CurrentProject.Assets
                    .FirstOrDefault(a => a.GlobalAssetId == globalAsset.Id);

                Asset projectAsset;
                if (existing != null)
                {
                    projectAsset = existing;
                }
                else
                {
                    var imported = await projectVm.AddAssetToCurrentProjectAsync(globalAsset.FilePath, "Image");
                    if (imported == null) return;
                    imported.GlobalAssetId = globalAsset.Id;
                    await projectVm.SaveProjectAsync();
                    projectAsset = imported;
                }

                // Now add a segment using the project-local asset (auto-creates track if collision)
                Dispatcher.Invoke(() =>
                {
                    var resultTrack = _viewModel.AddSegmentWithAutoTrack(track, dropTimeSeconds, projectAsset);
                    if (resultTrack != null)
                    {
                        UpdateSegmentLayout();
                        UpdateSegmentSelection();
                    }
                });
            }
            catch (System.Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to add global asset to project via timeline drop");
            }
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
                // Ctrl+Click track header → select all segments on this track
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    _viewModel.SelectionService.SelectTrack(track);
                    e.Handled = true;
                    return;
                }

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

        private async void TrackRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PodcastVideoEditor.Core.Models.Track track)
                await (_viewModel?.RemoveTrackAsync(track) ?? Task.CompletedTask);
        }
    }
}
