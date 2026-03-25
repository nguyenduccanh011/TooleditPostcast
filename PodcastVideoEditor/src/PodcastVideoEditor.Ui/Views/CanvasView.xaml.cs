using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Interaction logic for CanvasView.xaml
    /// </summary>
    public partial class CanvasView : UserControl
    {
        private CanvasViewModel? _viewModel;
        private Canvas? _mainCanvas;
        private MediaElement? _videoPreview;
        private Point _dragStartPoint;
        private bool _isDragging;
        private double _originalX;
        private double _originalY;
        // Resize state
        private double _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH;
        private double _resizeAspectRatio;
        // Throttle video position sync to ~30fps to avoid excessive frame decoding
        private DateTime _lastVideoSyncTime = DateTime.MinValue;
        private const int VideoSyncThrottleMs = 33; // ~30fps

        public CanvasView()
        {
            InitializeComponent();
            
            // Get x:Name references directly - NO FindChild needed
            _videoPreview = (MediaElement)FindName("VideoPreview");
            _mainCanvas = (Canvas)FindName("MainCanvas");
            
            Loaded += OnCanvasLoaded;
            Unloaded += OnCanvasUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Unsubscribe from old ViewModel
            if (e.OldValue is CanvasViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            // Subscribe to new ViewModel
            _viewModel = e.NewValue as CanvasViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                
                // Sync VideoSource if already set when DataContext changes
                if (_viewModel.VideoSource != null && _videoPreview != null)
                    SetVideoSource(_viewModel.VideoSource);
            }
        }

        private void OnCanvasLoaded(object sender, RoutedEventArgs e)
        {
            // Fallback: try FindName again if constructor didn't find them
            _videoPreview ??= (MediaElement?)FindName("VideoPreview");
            _mainCanvas ??= (Canvas?)FindName("MainCanvas");
            
            _viewModel?.OnCanvasReloaded();
            
            // Sync VideoSource if already set before Loaded fires
            if (_viewModel?.VideoSource != null && _videoPreview != null)
                SetVideoSource(_viewModel.VideoSource);
        }

        private void OnCanvasUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.StopVisualizerTimer();
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }

        private void RatioButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CanvasViewModel.VideoPosition) && _videoPreview != null)
            {
                SyncVideoPosition();
            }
            else if (e.PropertyName == nameof(CanvasViewModel.VideoSource))
            {
                if (_viewModel?.VideoSource != null && _videoPreview != null)
                    SetVideoSource(_viewModel.VideoSource);
                else if (_viewModel?.VideoSource == null && _videoPreview != null)
                    _videoPreview.Source = null;
            }
        }

        /// <summary>
        /// Sets MediaElement.Source and subscribes to MediaOpened event
        /// Called both from PropertyChanged handler and manual sync on Load
        /// </summary>
        private void SetVideoSource(Uri videoSource)
        {
            if (_videoPreview == null) return;

            try
            {
                _videoPreview.MediaOpened -= OnMediaOpened;
                _videoPreview.MediaFailed -= OnMediaFailed;
                _videoPreview.MediaOpened += OnMediaOpened;
                _videoPreview.MediaFailed += OnMediaFailed;

                _videoPreview.Source = videoSource;
                // LoadedBehavior="Manual" requires Play() to trigger loading
                _videoPreview.Play();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to set MediaElement.Source");
            }
        }

        private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Serilog.Log.Warning(e.ErrorException, "MediaElement failed: {Message}", e.ErrorException?.Message);
            if (_videoPreview != null)
                _videoPreview.MediaFailed -= OnMediaFailed;
        }

        private void OnMediaOpened(object? sender, RoutedEventArgs e)
        {
            if (_videoPreview != null)
            {
                _videoPreview.MediaOpened -= OnMediaOpened;
                // Play then Pause to render the first frame
                _videoPreview.Play();
                _videoPreview.Pause();
                SyncVideoPosition();
            }
        }

        private void SyncVideoPosition()
        {
            if (_videoPreview == null || _viewModel == null)
                return;

            // Throttle position sync — MediaElement with ScrubbingEnabled decodes a full frame
            var now = DateTime.UtcNow;
            if ((now - _lastVideoSyncTime).TotalMilliseconds < VideoSyncThrottleMs)
                return;
            _lastVideoSyncTime = now;

            try
            {
                // Sync MediaElement position with ViewModel
                var targetPosition = _viewModel.VideoPosition;
                if (_videoPreview.Position != targetPosition && _videoPreview.NaturalDuration.HasTimeSpan)
                {
                    var duration = _videoPreview.NaturalDuration.TimeSpan;
                    if (targetPosition <= duration)
                    {
                        _videoPreview.Position = targetPosition;
                    }
                }
            }
            catch
            {
                // Ignore position sync errors during media loading
            }
        }

        /// <summary>
        /// Handle mouse down on canvas (for deselection or drag start).
        /// </summary>
        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Click on empty canvas = deselect
            if (_viewModel != null && e.Source == sender)
            {
                _viewModel.SelectElement(null);
            }
        }

        /// <summary>
        /// Handle mouse down on canvas element (for selection and drag).
        /// Routes to the Grid wrapper that contains both the content and resize handles.
        /// </summary>
        private void OnCanvasElementMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_mainCanvas == null) return;

            // The sender is now the Grid wrapper; get the CanvasElement from DataContext
            FrameworkElement? fe = sender as FrameworkElement;
            if (fe?.DataContext is not CanvasElement element) return;

            _viewModel?.SelectElement(element);

            // Ensure keyboard focus stays on this control so canvas keyboard shortcuts
            // (Delete, arrows, Ctrl+D) fire via OnCanvasKeyDown rather than bubbling up
            // to MainWindow and being misrouted to the timeline.
            Keyboard.Focus(this);

            _dragStartPoint = e.GetPosition(_mainCanvas);
            _originalX = element.X;
            _originalY = element.Y;
            _isDragging = true;

            e.Handled = true;

            // Start drag on the Grid
            fe.MouseMove += OnCanvasElementMouseMove;
            fe.MouseUp += OnCanvasElementMouseUp;
            fe.CaptureMouse();
        }

        /// <summary>
        /// Handle mouse move for dragging elements.
        /// </summary>
        private void OnCanvasElementMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _viewModel?.SelectedElement == null || _mainCanvas == null)
                return;

            var currentPoint = e.GetPosition(_mainCanvas);
            var offsetX = currentPoint.X - _dragStartPoint.X;
            var offsetY = currentPoint.Y - _dragStartPoint.Y;

            var el = _viewModel.SelectedElement;
            el.X = Math.Max(0, Math.Min(_originalX + offsetX, _viewModel.CanvasWidth - el.Width));
            el.Y = Math.Max(0, Math.Min(_originalY + offsetY, _viewModel.CanvasHeight - el.Height));
        }

        /// <summary>
        /// Handle mouse up to stop dragging.
        /// </summary>
        private void OnCanvasElementMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging && _viewModel?.SelectedElement != null)
            {
                var el = _viewModel.SelectedElement;
                if (Math.Abs(el.X - _originalX) > 0.5 || Math.Abs(el.Y - _originalY) > 0.5)
                    _viewModel.UndoRedoService?.Record(new ElementMovedAction(el, _originalX, _originalY, el.X, el.Y));
            }

            _isDragging = false;

            if (sender is FrameworkElement fe)
            {
                fe.MouseMove -= OnCanvasElementMouseMove;
                fe.MouseUp -= OnCanvasElementMouseUp;
                fe.ReleaseMouseCapture();
            }
        }

        // ─── Resize handles ───────────────────────────────────────────────

        /// <summary>
        /// Called when the user starts dragging a resize Thumb. Captures original bounds.
        /// </summary>
        private void BeginResizeIfNeeded(CanvasElement el)
        {
            _resizeOrigX = el.X;
            _resizeOrigY = el.Y;
            _resizeOrigW = el.Width;
            _resizeOrigH = el.Height;
            _resizeAspectRatio = el.Width / Math.Max(el.Height, 1);
        }

        /// <summary>
        /// Handle DragDelta on corner/edge resize handles.
        /// Corner handles resize proportionally; MiddleLeft/MiddleRight adjust width only.
        /// </summary>
        private void OnResizeHandleDrag(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            var el = thumb.DataContext as CanvasElement;
            if (el == null || _viewModel == null) return;

            // Lazy-init on first delta
            if (_resizeOrigW == 0 && _resizeOrigH == 0)
                BeginResizeIfNeeded(el);

            var tag = thumb.Tag as string ?? "";
            const double minSize = 20;

            switch (tag)
            {
                case "MiddleLeft":
                {
                    // Horizontal-only: shrink/expand from left edge
                    var newW = Math.Max(minSize, el.Width - e.HorizontalChange);
                    var dx = el.Width - newW;
                    el.X = Math.Max(0, el.X + dx);
                    el.Width = newW;
                    break;
                }
                case "MiddleRight":
                {
                    // Horizontal-only: expand/shrink from right edge
                    var newW = Math.Max(minSize, el.Width + e.HorizontalChange);
                    newW = Math.Min(newW, _viewModel.CanvasWidth - el.X);
                    el.Width = newW;
                    break;
                }
                case "TopLeft":
                {
                    // Proportional resize from top-left corner
                    var delta = (e.HorizontalChange + e.VerticalChange) / 2;
                    var newW = Math.Max(minSize, el.Width - delta);
                    var newH = newW / _resizeAspectRatio;
                    if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                    var dx = el.Width - newW;
                    var dy = el.Height - newH;
                    el.X = Math.Max(0, el.X + dx);
                    el.Y = Math.Max(0, el.Y + dy);
                    el.Width = newW;
                    el.Height = newH;
                    break;
                }
                case "TopRight":
                {
                    var delta = (e.HorizontalChange - e.VerticalChange) / 2;
                    var newW = Math.Max(minSize, el.Width + delta);
                    newW = Math.Min(newW, _viewModel.CanvasWidth - el.X);
                    var newH = newW / _resizeAspectRatio;
                    if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                    var dy = el.Height - newH;
                    el.Y = Math.Max(0, el.Y + dy);
                    el.Width = newW;
                    el.Height = newH;
                    break;
                }
                case "BottomLeft":
                {
                    var delta = (-e.HorizontalChange + e.VerticalChange) / 2;
                    var newW = Math.Max(minSize, el.Width - delta);
                    var newH = newW / _resizeAspectRatio;
                    if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                    var dx = el.Width - newW;
                    el.X = Math.Max(0, el.X + dx);
                    el.Width = newW;
                    el.Height = Math.Min(newH, _viewModel.CanvasHeight - el.Y);
                    break;
                }
                case "BottomRight":
                {
                    var delta = (e.HorizontalChange + e.VerticalChange) / 2;
                    var newW = Math.Max(minSize, el.Width + delta);
                    newW = Math.Min(newW, _viewModel.CanvasWidth - el.X);
                    var newH = newW / _resizeAspectRatio;
                    if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                    el.Width = newW;
                    el.Height = Math.Min(newH, _viewModel.CanvasHeight - el.Y);
                    break;
                }
            }

            e.Handled = true;
        }

        /// <summary>
        /// Record undo action when resize is completed.
        /// </summary>
        private void OnResizeHandleCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            var el = thumb.DataContext as CanvasElement;
            if (el == null || _viewModel == null) return;

            // Only record if something actually changed
            if (Math.Abs(el.X - _resizeOrigX) > 0.5 || Math.Abs(el.Y - _resizeOrigY) > 0.5 ||
                Math.Abs(el.Width - _resizeOrigW) > 0.5 || Math.Abs(el.Height - _resizeOrigH) > 0.5)
            {
                _viewModel.UndoRedoService?.Record(new ElementResizedAction(
                    el, _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH,
                    el.X, el.Y, el.Width, el.Height));
            }

            // Reset for next resize
            _resizeOrigX = _resizeOrigY = _resizeOrigW = _resizeOrigH = 0;
            e.Handled = true;
        }

        /// <summary>
        /// Handle keyboard shortcuts.
        /// </summary>
        private void OnCanvasKeyDown(object sender, KeyEventArgs e)
        {
            // ✅ Space bar for playback toggle (Play/Pause)
            if (e.Key == Key.Space)
            {
                if (_viewModel != null)
                {
                    if (_viewModel.TogglePlayPauseCommand.CanExecute(null))
                    {
                        _viewModel.TogglePlayPauseCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (_viewModel?.SelectedElement == null)
                return;

            switch (e.Key)
            {
                case Key.Delete:
                    _viewModel.DeleteSelectedElementCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.D when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    _viewModel.DuplicateElementCommand.Execute(null);
                    e.Handled = true;
                    break;

                // Arrow keys for fine positioning (clamped to canvas bounds)
                case Key.Left:
                {
                    double nudge = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    _viewModel.SelectedElement.X = Math.Max(0, _viewModel.SelectedElement.X - nudge);
                    e.Handled = true;
                    break;
                }
                case Key.Right:
                {
                    double nudge = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    _viewModel.SelectedElement.X = Math.Min(
                        _viewModel.CanvasWidth - _viewModel.SelectedElement.Width,
                        _viewModel.SelectedElement.X + nudge);
                    e.Handled = true;
                    break;
                }
                case Key.Up:
                {
                    double nudge = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    _viewModel.SelectedElement.Y = Math.Max(0, _viewModel.SelectedElement.Y - nudge);
                    e.Handled = true;
                    break;
                }
                case Key.Down:
                {
                    double nudge = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    _viewModel.SelectedElement.Y = Math.Min(
                        _viewModel.CanvasHeight - _viewModel.SelectedElement.Height,
                        _viewModel.SelectedElement.Y + nudge);
                    e.Handled = true;
                    break;
                }
            }
        }
    }
}
