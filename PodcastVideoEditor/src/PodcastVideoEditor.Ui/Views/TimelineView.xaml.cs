using PodcastVideoEditor.Core.Models;
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
using Serilog;

namespace PodcastVideoEditor.Ui.Views
{
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
        private double _resizeStartX;
        private double _segmentOriginalEndTime;
        private double _segmentOriginalStartTime;
        private double _resizeDeltaX;
        private double _resizeLeftDeltaX;
        private double _resizeLeftOriginalStartTime;
        private double _moveDeltaX;
        private readonly DispatcherTimer _zoomTimer;
        private double _pendingZoomFactor = 1.0;
        private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;
        private NotifyCollectionChangedEventHandler? _segmentsCollectionChangedHandler;

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

                // Update canvas width when timeline width changes
                TimelineCanvas.Width = _viewModel.TimelineWidth;
                // PlayheadLine spans full height (288) in XAML

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
                    }
                    else if (args.PropertyName == nameof(TimelineViewModel.TimelineWidth))
                    {
                        TimelineCanvas.Width = _viewModel.TimelineWidth;
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

                // Subscribe to segments collection changes
                _segmentsCollectionChangedHandler = (s, args) =>
                {
                    UpdateSegmentLayout();
                };
                _viewModel.Segments.CollectionChanged += _segmentsCollectionChangedHandler;

                Log.Information("TimelineView loaded, ViewModel connected");
            }
        }

        private void TimelineView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (TimelineScroller != null)
                TimelineScroller.PreviewMouseWheel -= TimelineScroller_PreviewMouseWheel;
            if (_viewModel != null && _viewModelPropertyChangedHandler != null)
                _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
            if (_viewModel != null && _segmentsCollectionChangedHandler != null)
                _viewModel.Segments.CollectionChanged -= _segmentsCollectionChangedHandler;
            _zoomTimer.Stop();
            Log.Information("TimelineView unloaded");
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
        /// </summary>
        private void UpdateSegmentLayout()
        {
            if (_viewModel == null || SegmentsItemsControl == null)
                return;

            try
            {
                // Get all segment borders from ItemsControl
                for (int i = 0; i < SegmentsItemsControl.Items.Count; i++)
                {
                    var presenter = SegmentsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                    if (presenter == null)
                        continue;

                    presenter.ApplyTemplate();

                    if (presenter.Content is not Segment segment)
                        continue;

                    var border = FindVisualChild<Border>(presenter);
                    if (border == null)
                        continue;

                    // Calculate position and width (proportional to timeline; no large minimum so 0.1s looks like 0.1s)
                    double pixelX = _viewModel.TimeToPixels(segment.StartTime);
                    double pixelWidth = _viewModel.TimeToPixels(segment.EndTime - segment.StartTime);
                    pixelWidth = Math.Max(4, pixelWidth); // tiny min so resize handle stays grabbable

                    // Set Canvas position on ContentPresenter (Canvas's direct child), NOT on Border
                    Canvas.SetLeft(presenter, pixelX);
                    Canvas.SetTop(presenter, 10);
                    border.Width = pixelWidth;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error updating segment layout");
            }
        }

        /// <summary>
        /// Update visual selection state of segments.
        /// </summary>
        private void UpdateSegmentSelection()
        {
            if (_viewModel == null || SegmentsItemsControl == null)
                return;

            try
            {
                for (int i = 0; i < SegmentsItemsControl.Items.Count; i++)
                {
                    var presenter = SegmentsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                    if (presenter == null)
                        continue;

                    presenter.ApplyTemplate();

                    if (presenter.Content is not Segment segment)
                        continue;

                    var border = FindVisualChild<Border>(presenter);
                    if (border == null)
                        continue;

                    bool isSelected = segment == _viewModel.SelectedSegment;

                    if (isSelected)
                    {
                        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xb5, 0xf6));
                        border.BorderThickness = new Thickness(2);
                        border.Background = new SolidColorBrush(Color.FromRgb(0x45, 0x5a, 0x64));
                    }
                    else
                    {
                        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x6e, 0x7a));
                        border.BorderThickness = new Thickness(1);
                        border.Background = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4f));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error updating segment selection");
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
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
            {
                Focus();
                _viewModel?.SelectSegment(segment);
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
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
            {
                _isResizingLeft = false;
                _isResizingSegment = true;
                _resizeStartX = e.HorizontalOffset;
                _segmentOriginalEndTime = segment.EndTime;
                _resizeDeltaX = 0;
                grid.Cursor = Cursors.SizeWE;

                Log.Debug("Resize right started for segment: {SegmentId}", segment.Id);
            }
        }

        /// <summary>
        /// Start segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
            {
                _isResizingSegment = false;
                _isResizingLeft = true;
                _resizeLeftOriginalStartTime = segment.StartTime;
                _resizeLeftDeltaX = 0;
                grid.Cursor = Cursors.SizeWE;

                Log.Debug("Resize left started for segment: {SegmentId}", segment.Id);
            }
        }

        /// <summary>
        /// Handle segment resize (right edge) drag.
        /// </summary>
        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isResizingSegment || _viewModel == null)
                return;

            if (sender is Thumb thumb && 
                thumb.Parent is Grid grid && 
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
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
                    EnsureSegmentVisibleInScroll(segment);
            }
        }

        /// <summary>
        /// Handle segment resize (left edge) drag — extend/shrink duration toward earlier time.
        /// </summary>
        private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isResizingLeft || _viewModel == null)
                return;

            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
            {
                _resizeLeftDeltaX += e.HorizontalChange;
                double timeDelta = _viewModel.PixelsToTime(_resizeLeftDeltaX);
                double newStartTime = _resizeLeftOriginalStartTime + timeDelta;

                if (newStartTime < 0)
                    newStartTime = 0;
                if (newStartTime >= segment.EndTime)
                    newStartTime = segment.EndTime - _viewModel.GridSize;

                bool updated = _viewModel.UpdateSegmentTiming(segment, newStartTime, segment.EndTime);

                const double tolerance = 0.01;
                if (updated && Math.Abs(segment.StartTime - newStartTime) > tolerance)
                {
                    _resizeLeftOriginalStartTime = segment.StartTime;
                    _resizeLeftDeltaX = 0;
                }

                if (updated)
                    EnsureSegmentVisibleInScroll(segment);
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
                grid.Cursor = Cursors.Arrow;
            Log.Debug("Resize right completed");
        }

        /// <summary>
        /// End segment resize (left edge) operation.
        /// </summary>
        private void ResizeLeftThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isResizingLeft = false;
            _resizeLeftDeltaX = 0;
            if (sender is Thumb thumb && thumb.Parent is Grid grid)
                grid.Cursor = Cursors.Arrow;
            Log.Debug("Resize left completed");
        }

        /// <summary>
        /// Start segment move operation.
        /// </summary>
        private void MoveThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb &&
                thumb.Parent is Grid grid &&
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
            {
                _isDraggingSegment = true;
                _segmentOriginalStartTime = segment.StartTime;
                _segmentOriginalEndTime = segment.EndTime;
                _moveDeltaX = 0;
                grid.Cursor = Cursors.SizeAll;
                _viewModel?.SelectSegment(segment);
                Log.Debug("Move started for segment: {SegmentId}", segment.Id);
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
                grid.Parent is Border border &&
                border.DataContext is Segment segment)
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
                    EnsureSegmentVisibleInScroll(segment);
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
            }
            Log.Debug("Move completed");
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
            HandlePlayheadClick(e.GetPosition(TimelineCanvas).X);
            e.Handled = true;
        }

        /// <summary>
        /// Handle dragging to move playhead.
        /// </summary>
        private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                HandlePlayheadClick(e.GetPosition(TimelineCanvas).X);
            }
        }

        /// <summary>
        /// Handle releasing playhead drag.
        /// </summary>
        private void TimelineCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingPlayhead = false;
        }

        /// <summary>
        /// Helper to move playhead and seek audio to clicked position.
        /// Must call SeekTo (not just set PlayheadPosition) else sync loop overwrites it.
        /// </summary>
        private void HandlePlayheadClick(double pixelX)
        {
            if (_viewModel == null)
                return;

            double newTime = _viewModel.PixelsToTime(Math.Max(0, pixelX));
            _viewModel.SeekTo(newTime);
        }

        /// <summary>
        /// Handle clicking on ruler to move playhead.
        /// </summary>
        private void RulerBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null)
                return;

            Focus();
            _isDraggingPlayhead = true;
            
            // Get click position relative to the ruler control
            var position = e.GetPosition(RulerControl);
            double newTime = _viewModel.PixelsToTime(Math.Max(0, position.X));
            _viewModel.SeekTo(newTime);
            e.Handled = true;
        }

        /// <summary>
        /// Handle dragging on ruler to move playhead.
        /// </summary>
        private void RulerBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPlayhead && _viewModel != null)
            {
                var position = e.GetPosition(RulerControl);
                double newTime = _viewModel.PixelsToTime(Math.Max(0, position.X));
                _viewModel.SeekTo(newTime);
            }
        }

        /// <summary>
        /// Handle releasing drag on ruler.
        /// </summary>
        private void RulerBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
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
    }
}


