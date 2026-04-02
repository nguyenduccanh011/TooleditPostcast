using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;

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
        // Track sibling text elements for synchronized drag
        private List<(TextOverlayElement element, double origX, double origY)>? _dragSiblings;
        // Resize state
        private double _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH;
        private double _resizeAspectRatio;
        private double _resizeOrigFontSize; // Text element: original font size for proportional corner scaling
        private TextSizingMode _resizeOrigSizingMode; // Text element: original sizing mode
        // Track sibling text elements for synchronized resize
        private List<(TextOverlayElement element, double origX, double origY, double origW, double origH, double origFontSize, TextSizingMode origSizingMode)>? _resizeSiblings;
        // Rotation state
        private double _rotationOrigAngle;
        private Point _rotationCenter;
        // Snap guide lines
        private Line? _snapGuideH;
        private Line? _snapGuideV;
        private const double SnapThreshold = 8.0; // px distance to snap
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
            _snapGuideH ??= (Line?)FindName("SnapGuideH");
            _snapGuideV ??= (Line?)FindName("SnapGuideV");
            
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

            // If this is a TextOverlayElement on a text track, capture siblings for synchronized drag
            _dragSiblings = null;
            if (element is TextOverlayElement textEl && _viewModel != null)
            {
                var track = _viewModel.FindOwnerTrack(textEl);
                if (track != null)
                {
                    var siblings = _viewModel.GetTrackSiblingTextElements(track);
                    _dragSiblings = siblings
                        .Where(s => !ReferenceEquals(s, textEl))
                        .Select(s => (s, s.X, s.Y))
                        .ToList();
                }
            }

            e.Handled = true;

            // Start drag on the Grid
            fe.MouseMove += OnCanvasElementMouseMove;
            fe.MouseUp += OnCanvasElementMouseUp;
            fe.CaptureMouse();
        }

        /// <summary>
        /// Handle mouse move for dragging elements with smart snap guides.
        /// </summary>
        private void OnCanvasElementMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _viewModel?.SelectedElement == null || _mainCanvas == null)
                return;

            var currentPoint = e.GetPosition(_mainCanvas);
            var offsetX = currentPoint.X - _dragStartPoint.X;
            var offsetY = currentPoint.Y - _dragStartPoint.Y;

            var el = _viewModel.SelectedElement;
            var newX = Math.Max(0, Math.Min(_originalX + offsetX, _viewModel.CanvasWidth - el.Width));
            var newY = Math.Max(0, Math.Min(_originalY + offsetY, _viewModel.CanvasHeight - el.Height));

            // Smart snap: center of canvas and edges of other elements
            var canvasW = _viewModel.CanvasWidth;
            var canvasH = _viewModel.CanvasHeight;
            var elCenterX = newX + el.Width / 2;
            var elCenterY = newY + el.Height / 2;
            bool snappedH = false, snappedV = false;
            double snapLineY = 0, snapLineX = 0;

            // Snap to canvas center horizontal (element center Y → canvas center Y)
            if (Math.Abs(elCenterY - canvasH / 2) < SnapThreshold)
            {
                newY = canvasH / 2 - el.Height / 2;
                snapLineY = canvasH / 2;
                snappedH = true;
            }
            // Snap to canvas center vertical (element center X → canvas center X)
            if (Math.Abs(elCenterX - canvasW / 2) < SnapThreshold)
            {
                newX = canvasW / 2 - el.Width / 2;
                snapLineX = canvasW / 2;
                snappedV = true;
            }
            // Snap to canvas top edge
            if (!snappedH && Math.Abs(newY) < SnapThreshold)
            {
                newY = 0;
                snapLineY = 0;
                snappedH = true;
            }
            // Snap to canvas bottom edge
            if (!snappedH && Math.Abs(newY + el.Height - canvasH) < SnapThreshold)
            {
                newY = canvasH - el.Height;
                snapLineY = canvasH;
                snappedH = true;
            }
            // Snap to canvas left edge
            if (!snappedV && Math.Abs(newX) < SnapThreshold)
            {
                newX = 0;
                snapLineX = 0;
                snappedV = true;
            }
            // Snap to canvas right edge
            if (!snappedV && Math.Abs(newX + el.Width - canvasW) < SnapThreshold)
            {
                newX = canvasW - el.Width;
                snapLineX = canvasW;
                snappedV = true;
            }

            el.X = newX;
            el.Y = newY;

            // Show/hide snap guide lines
            UpdateSnapGuides(snappedH, snapLineY, snappedV, snapLineX, canvasW, canvasH);

            // Synchronize sibling text elements on the same track
            if (_dragSiblings != null)
            {
                foreach (var (sibling, origX, origY) in _dragSiblings)
                {
                    sibling.X = Math.Max(0, Math.Min(origX + offsetX, _viewModel.CanvasWidth - sibling.Width));
                    sibling.Y = Math.Max(0, Math.Min(origY + offsetY, _viewModel.CanvasHeight - sibling.Height));
                }
            }
        }

        /// <summary>
        /// Handle mouse up to stop dragging.
        /// </summary>
        private void OnCanvasElementMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging && _viewModel?.SelectedElement != null)
            {
                var el = _viewModel.SelectedElement;
                bool moved = Math.Abs(el.X - _originalX) > 0.5 || Math.Abs(el.Y - _originalY) > 0.5;

                if (moved)
                {
                    if (_dragSiblings != null && _dragSiblings.Count > 0)
                    {
                        // Group undo: primary element + all siblings
                        var actions = new List<IUndoableAction>();
                        actions.Add(new ElementMovedAction(el, _originalX, _originalY, el.X, el.Y));
                        foreach (var (sibling, origX, origY) in _dragSiblings)
                            actions.Add(new ElementMovedAction(sibling, origX, origY, sibling.X, sibling.Y));
                        _viewModel.UndoRedoService?.Record(new CompoundAction($"Move text elements on track", actions));

                        // Update track template with new position
                        if (el is TextOverlayElement textEl)
                            _viewModel.PropagateTextElementStyle(textEl);
                    }
                    else
                    {
                        _viewModel.UndoRedoService?.Record(new ElementMovedAction(el, _originalX, _originalY, el.X, el.Y));
                    }
                }
            }

            _isDragging = false;
            _dragSiblings = null;
            HideSnapGuides();

            if (sender is FrameworkElement fe)
            {
                fe.MouseMove -= OnCanvasElementMouseMove;
                fe.MouseUp -= OnCanvasElementMouseUp;
                fe.ReleaseMouseCapture();
            }
        }

        // ─── Snap guides ──────────────────────────────────────────────────

        /// <summary>
        /// Show or hide snap guide lines during element drag.
        /// </summary>
        private void UpdateSnapGuides(bool showH, double hY, bool showV, double vX,
                                       double canvasW, double canvasH)
        {
            if (_snapGuideH != null)
            {
                if (showH)
                {
                    _snapGuideH.X1 = 0;
                    _snapGuideH.X2 = canvasW;
                    _snapGuideH.Y1 = hY;
                    _snapGuideH.Y2 = hY;
                    _snapGuideH.Visibility = Visibility.Visible;
                }
                else
                {
                    _snapGuideH.Visibility = Visibility.Collapsed;
                }
            }
            if (_snapGuideV != null)
            {
                if (showV)
                {
                    _snapGuideV.X1 = vX;
                    _snapGuideV.X2 = vX;
                    _snapGuideV.Y1 = 0;
                    _snapGuideV.Y2 = canvasH;
                    _snapGuideV.Visibility = Visibility.Visible;
                }
                else
                {
                    _snapGuideV.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Hide all snap guide lines.
        /// </summary>
        private void HideSnapGuides()
        {
            if (_snapGuideH != null) _snapGuideH.Visibility = Visibility.Collapsed;
            if (_snapGuideV != null) _snapGuideV.Visibility = Visibility.Collapsed;
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

            // Capture text-specific state for font scaling
            if (el is TextOverlayElement textEl)
            {
                _resizeOrigFontSize = textEl.FontSize;
                _resizeOrigSizingMode = textEl.SizingMode;
            }

            // Capture sibling state for synchronized resize on text tracks
            _resizeSiblings = null;
            if (el is TextOverlayElement primaryText && _viewModel != null)
            {
                var track = _viewModel.FindOwnerTrack(primaryText);
                if (track != null)
                {
                    var siblings = _viewModel.GetTrackSiblingTextElements(track);
                    _resizeSiblings = siblings
                        .Where(s => !ReferenceEquals(s, primaryText))
                        .Select(s => (s, s.X, s.Y, s.Width, s.Height, s.FontSize, s.SizingMode))
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Handle DragDelta on corner/edge resize handles.
        /// For text elements: corner handles scale font proportionally, edge handles reflow text.
        /// For other elements: corner handles resize proportionally, edge handles adjust single axis.
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
            bool isTextElement = el is TextOverlayElement;
            bool isCorner = tag is "TopLeft" or "TopRight" or "BottomLeft" or "BottomRight";
            bool isHorizontalEdge = tag is "MiddleLeft" or "MiddleRight";

            // ── Text element: special resize behavior ──
            if (isTextElement && el is TextOverlayElement textEl)
            {
                if (isCorner)
                {
                    // Corner handles → proportional font scaling (like CapCut/Canva)
                    ResizeTextCorner(textEl, tag, e, minSize);
                }
                else if (isHorizontalEdge)
                {
                    // Horizontal edge → width-only, height auto-reflows
                    ResizeTextHorizontalEdge(textEl, tag, e, minSize);
                }
                else
                {
                    // Vertical edge (MiddleTop/MiddleBottom) → fixed height mode
                    ResizeGeneric(el, tag, e, minSize, lockAspect: false);
                }

                // Synchronize siblings
                SyncTextSiblings(textEl);
            }
            else
            {
                // ── Non-text elements: original behavior ──
                bool lockAspect = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
                ResizeGeneric(el, tag, e, minSize, lockAspect);

                if (_resizeSiblings != null)
                {
                    foreach (var (sibling, _, _, _, _, _, _) in _resizeSiblings)
                    {
                        sibling.X = el.X;
                        sibling.Y = el.Y;
                        sibling.Width = el.Width;
                        sibling.Height = el.Height;
                    }
                }
            }

            e.Handled = true;
        }

        /// <summary>
        /// Corner resize for text: scale FontSize proportionally with bounding box.
        /// Maintains aspect ratio of text appearance (font grows/shrinks with frame).
        /// </summary>
        private void ResizeTextCorner(TextOverlayElement el, string tag, DragDeltaEventArgs e, double minSize)
        {
            // Compute proportional scale from corner drag delta
            double newW, newH, dx = 0, dy = 0;
            var delta = tag switch
            {
                "TopLeft" => (e.HorizontalChange + e.VerticalChange) / 2,
                "TopRight" => (e.HorizontalChange - e.VerticalChange) / 2,
                "BottomLeft" => (-e.HorizontalChange + e.VerticalChange) / 2,
                "BottomRight" => (e.HorizontalChange + e.VerticalChange) / 2,
                _ => 0.0
            };

            bool growsFromDelta = tag is "BottomRight" or "TopRight" or "BottomLeft";
            bool shrinks = tag is "TopLeft";

            // Calculate new width based on corner direction
            if (tag is "TopLeft" or "BottomLeft")
                newW = Math.Max(minSize, _resizeOrigW - delta);
            else
                newW = Math.Max(minSize, _resizeOrigW + delta);

            newW = Math.Min(newW, _viewModel!.CanvasWidth - (tag.Contains("Left") ? 0 : el.X));

            // Scale factor relative to original width
            var scaleFactor = newW / _resizeOrigW;
            newH = _resizeOrigH * scaleFactor;
            if (newH < minSize) { newH = minSize; scaleFactor = newH / _resizeOrigH; newW = _resizeOrigW * scaleFactor; }

            // Scale font size proportionally, clamped to [8, 200]
            var newFontSize = Math.Clamp(_resizeOrigFontSize * scaleFactor, 8, 200);

            // Compute position offset for anchored corners
            switch (tag)
            {
                case "TopLeft":
                    dx = _resizeOrigW - newW;
                    dy = _resizeOrigH - newH;
                    el.X = Math.Max(0, _resizeOrigX + dx);
                    el.Y = Math.Max(0, _resizeOrigY + dy);
                    break;
                case "TopRight":
                    dy = _resizeOrigH - newH;
                    el.Y = Math.Max(0, _resizeOrigY + dy);
                    break;
                case "BottomLeft":
                    dx = _resizeOrigW - newW;
                    el.X = Math.Max(0, _resizeOrigX + dx);
                    break;
                case "BottomRight":
                    // Anchored at top-left, no position change
                    break;
            }

            el.Width = newW;
            el.Height = Math.Min(newH, _viewModel.CanvasHeight - el.Y);
            el.FontSize = newFontSize;
        }

        /// <summary>
        /// Horizontal edge resize for text: changes width only, keeps AutoHeight mode
        /// so text reflows and height adjusts automatically to fit wrapped lines.
        /// </summary>
        private void ResizeTextHorizontalEdge(TextOverlayElement el, string tag, DragDeltaEventArgs e, double minSize)
        {
            switch (tag)
            {
                case "MiddleLeft":
                {
                    var newW = Math.Max(minSize, el.Width - e.HorizontalChange);
                    var widthDelta = el.Width - newW;
                    el.X = Math.Max(0, el.X + widthDelta);
                    el.Width = newW;
                    break;
                }
                case "MiddleRight":
                {
                    var newW = Math.Max(minSize, el.Width + e.HorizontalChange);
                    newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                    el.Width = newW;
                    break;
                }
            }

            // Keep AutoHeight so WPF layout auto-adjusts height for word-wrap reflow
            el.SizingMode = TextSizingMode.AutoHeight;
        }

        /// <summary>
        /// Sync sibling text elements with current element's size, position, and font.
        /// </summary>
        private void SyncTextSiblings(TextOverlayElement source)
        {
            if (_resizeSiblings == null) return;
            foreach (var (sibling, _, _, _, _, _, _) in _resizeSiblings)
            {
                sibling.X = source.X;
                sibling.Y = source.Y;
                sibling.Width = source.Width;
                sibling.Height = source.Height;
                sibling.FontSize = source.FontSize;
                sibling.SizingMode = source.SizingMode;
            }
        }

        /// <summary>
        /// Generic resize logic for non-text elements (images, visualizers, etc.).
        /// </summary>
        private void ResizeGeneric(CanvasElement el, string tag, DragDeltaEventArgs e, double minSize, bool lockAspect)
        {
            switch (tag)
            {
                case "MiddleLeft":
                {
                    var newW = Math.Max(minSize, el.Width - e.HorizontalChange);
                    var dx = el.Width - newW;
                    el.X = Math.Max(0, el.X + dx);
                    el.Width = newW;
                    break;
                }
                case "MiddleRight":
                {
                    var newW = Math.Max(minSize, el.Width + e.HorizontalChange);
                    newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                    el.Width = newW;
                    break;
                }
                case "TopLeft":
                {
                    if (lockAspect)
                    {
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
                    }
                    else
                    {
                        var newW = Math.Max(minSize, el.Width - e.HorizontalChange);
                        var newH = Math.Max(minSize, el.Height - e.VerticalChange);
                        el.X = Math.Max(0, el.X + (el.Width - newW));
                        el.Y = Math.Max(0, el.Y + (el.Height - newH));
                        el.Width = newW;
                        el.Height = newH;
                    }
                    break;
                }
                case "TopRight":
                {
                    if (lockAspect)
                    {
                        var delta = (e.HorizontalChange - e.VerticalChange) / 2;
                        var newW = Math.Max(minSize, el.Width + delta);
                        newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                        var newH = newW / _resizeAspectRatio;
                        if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                        var dy = el.Height - newH;
                        el.Y = Math.Max(0, el.Y + dy);
                        el.Width = newW;
                        el.Height = newH;
                    }
                    else
                    {
                        var newW = Math.Max(minSize, el.Width + e.HorizontalChange);
                        newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                        var newH = Math.Max(minSize, el.Height - e.VerticalChange);
                        el.Y = Math.Max(0, el.Y + (el.Height - newH));
                        el.Width = newW;
                        el.Height = newH;
                    }
                    break;
                }
                case "BottomLeft":
                {
                    if (lockAspect)
                    {
                        var delta = (-e.HorizontalChange + e.VerticalChange) / 2;
                        var newW = Math.Max(minSize, el.Width - delta);
                        var newH = newW / _resizeAspectRatio;
                        if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                        var dx = el.Width - newW;
                        el.X = Math.Max(0, el.X + dx);
                        el.Width = newW;
                        el.Height = Math.Min(newH, _viewModel!.CanvasHeight - el.Y);
                    }
                    else
                    {
                        var newW = Math.Max(minSize, el.Width - e.HorizontalChange);
                        var newH = Math.Max(minSize, el.Height + e.VerticalChange);
                        newH = Math.Min(newH, _viewModel!.CanvasHeight - el.Y);
                        el.X = Math.Max(0, el.X + (el.Width - newW));
                        el.Width = newW;
                        el.Height = newH;
                    }
                    break;
                }
                case "BottomRight":
                {
                    if (lockAspect)
                    {
                        var delta = (e.HorizontalChange + e.VerticalChange) / 2;
                        var newW = Math.Max(minSize, el.Width + delta);
                        newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                        var newH = newW / _resizeAspectRatio;
                        if (newH < minSize) { newH = minSize; newW = newH * _resizeAspectRatio; }
                        el.Width = newW;
                        el.Height = Math.Min(newH, _viewModel.CanvasHeight - el.Y);
                    }
                    else
                    {
                        var newW = Math.Max(minSize, el.Width + e.HorizontalChange);
                        newW = Math.Min(newW, _viewModel!.CanvasWidth - el.X);
                        var newH = Math.Max(minSize, el.Height + e.VerticalChange);
                        newH = Math.Min(newH, _viewModel.CanvasHeight - el.Y);
                        el.Width = newW;
                        el.Height = newH;
                    }
                    break;
                }
                case "MiddleTop":
                {
                    var newH = Math.Max(minSize, el.Height - e.VerticalChange);
                    var dy = el.Height - newH;
                    el.Y = Math.Max(0, el.Y + dy);
                    el.Height = newH;
                    break;
                }
                case "MiddleBottom":
                {
                    var newH = Math.Max(minSize, el.Height + e.VerticalChange);
                    newH = Math.Min(newH, _viewModel!.CanvasHeight - el.Y);
                    el.Height = newH;
                    break;
                }
            }
        }

        /// <summary>
        /// Record undo action when resize is completed.
        /// For text elements: uses TextElementResizedAction to also track FontSize + SizingMode.
        /// Corner resize preserves current SizingMode; horizontal edge keeps AutoHeight.
        /// </summary>
        private void OnResizeHandleCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            var el = thumb.DataContext as CanvasElement;
            if (el == null || _viewModel == null) return;

            var tag = thumb.Tag as string ?? "";
            bool changed = Math.Abs(el.X - _resizeOrigX) > 0.5 || Math.Abs(el.Y - _resizeOrigY) > 0.5 ||
                           Math.Abs(el.Width - _resizeOrigW) > 0.5 || Math.Abs(el.Height - _resizeOrigH) > 0.5;

            if (changed)
            {
                if (el is TextOverlayElement resizedText)
                {
                    // Determine final SizingMode based on which handle was used:
                    // - Corner handles: keep current mode (font scaled, no overflow possible)
                    // - Horizontal edge: AutoHeight (already set during drag)
                    // - Vertical edge (MiddleTop/MiddleBottom): Fixed (user explicitly set height)
                    bool isVerticalEdge = tag is "MiddleTop" or "MiddleBottom";
                    if (isVerticalEdge)
                    {
                        resizedText.SizingMode = TextSizingMode.Fixed;
                        foreach (var (sibling, _, _, _, _, _, _) in _resizeSiblings ?? [])
                            sibling.SizingMode = TextSizingMode.Fixed;
                    }

                    // Record text-specific undo with FontSize + SizingMode
                    if (_resizeSiblings != null && _resizeSiblings.Count > 0)
                    {
                        var actions = new List<IUndoableAction>();
                        actions.Add(new TextElementResizedAction(resizedText,
                            _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH, _resizeOrigFontSize, _resizeOrigSizingMode,
                            el.X, el.Y, el.Width, el.Height, resizedText.FontSize, resizedText.SizingMode));
                        foreach (var (sibling, origX, origY, origW, origH, origFontSize, origSizingMode) in _resizeSiblings)
                            actions.Add(new TextElementResizedAction(sibling,
                                origX, origY, origW, origH, origFontSize, origSizingMode,
                                sibling.X, sibling.Y, sibling.Width, sibling.Height, sibling.FontSize, sibling.SizingMode));
                        _viewModel.UndoRedoService?.Record(new CompoundAction("Resize text elements on track", actions));
                        _viewModel.PropagateTextElementStyle(resizedText);
                    }
                    else
                    {
                        _viewModel.UndoRedoService?.Record(new TextElementResizedAction(resizedText,
                            _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH, _resizeOrigFontSize, _resizeOrigSizingMode,
                            el.X, el.Y, el.Width, el.Height, resizedText.FontSize, resizedText.SizingMode));
                    }
                }
                else
                {
                    // Non-text element: original undo logic
                    if (_resizeSiblings != null && _resizeSiblings.Count > 0)
                    {
                        var actions = new List<IUndoableAction>();
                        actions.Add(new ElementResizedAction(el, _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH, el.X, el.Y, el.Width, el.Height));
                        foreach (var (sibling, origX, origY, origW, origH, _, _) in _resizeSiblings)
                            actions.Add(new ElementResizedAction(sibling, origX, origY, origW, origH, sibling.X, sibling.Y, sibling.Width, sibling.Height));
                        _viewModel.UndoRedoService?.Record(new CompoundAction("Resize elements on track", actions));
                    }
                    else
                    {
                        _viewModel.UndoRedoService?.Record(new ElementResizedAction(
                            el, _resizeOrigX, _resizeOrigY, _resizeOrigW, _resizeOrigH,
                            el.X, el.Y, el.Width, el.Height));
                    }
                }
            }

            // Reset for next resize
            _resizeOrigX = _resizeOrigY = _resizeOrigW = _resizeOrigH = 0;
            _resizeOrigFontSize = 0;
            _resizeSiblings = null;
            e.Handled = true;
        }

        // ─── Rotation handle ──────────────────────────────────────────────

        // ─── Text element auto-sizing ──────────────────────────────────────

        /// <summary>
        /// Fires when the DataTemplate root Grid for a TextOverlayElement changes size.
        /// In AutoHeight mode the Grid is un-constrained, so its ActualHeight equals the
        /// measured content height — we write that back to the model so the rest of the
        /// pipeline (render, hit-testing, etc.) stays consistent with what the user sees.
        /// </summary>
        private void OnTextElementGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement fe
                && fe.DataContext is TextOverlayElement el
                && el.SizingMode == TextSizingMode.AutoHeight
                && e.NewSize.Height > 0
                && !double.IsNaN(e.NewSize.Height)
                && Math.Abs(e.NewSize.Height - el.Height) > 0.5)
            {
                el.Height = Math.Ceiling(e.NewSize.Height);
            }
        }

        /// <summary>
        /// Fires when the TextBlock inside a TextOverlayElement template changes size.
        /// In Fixed mode this lets us detect whether content overflows the box and show
        /// the overflow indicator without a costly visual-tree traversal.
        /// </summary>
        private void OnTextBlockSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement fe
                && fe.DataContext is TextOverlayElement el
                && el.SizingMode == TextSizingMode.Fixed)
            {
                // Padding="8,4" contributes 8px total vertical padding
                el.IsOverflowing = e.NewSize.Height + 8 > el.Height;
            }
        }

        /// <summary>
        /// Handle DragDelta on the rotation handle. Computes angle from element center to mouse.
        /// </summary>
        private void OnRotationHandleDrag(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb || _mainCanvas == null) return;
            var el = thumb.DataContext as CanvasElement;
            if (el == null || _viewModel == null) return;

            // Lazy-init on first delta
            if (_rotationCenter == default)
            {
                _rotationOrigAngle = el.Rotation;
                _rotationCenter = new Point(el.X + el.Width / 2, el.Y + el.Height / 2);
            }

            // Get current mouse position relative to the main canvas
            var mousePos = Mouse.GetPosition(_mainCanvas);
            var dx = mousePos.X - _rotationCenter.X;
            var dy = mousePos.Y - _rotationCenter.Y;
            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90; // +90 because handle is above

            // Snap to 15° increments when Shift is held
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                angle = Math.Round(angle / 15.0) * 15.0;

            el.Rotation = angle;
            e.Handled = true;
        }

        /// <summary>
        /// Record undo action when rotation drag is completed.
        /// </summary>
        private void OnRotationHandleCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            var el = thumb.DataContext as CanvasElement;
            if (el == null || _viewModel == null) return;

            if (Math.Abs(el.Rotation - _rotationOrigAngle) > 0.5)
            {
                _viewModel.UndoRedoService?.Record(new ElementRotatedAction(el, _rotationOrigAngle, el.Rotation));
            }

            _rotationCenter = default;
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

                // Flip H: Ctrl+H
                case Key.H when (Keyboard.Modifiers & ModifierKeys.Control) != 0
                              && (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                    _viewModel.FlipHorizontalCommand.Execute(null);
                    e.Handled = true;
                    break;

                // Flip V: Ctrl+Shift+H
                case Key.H when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift))
                              == (ModifierKeys.Control | ModifierKeys.Shift):
                    _viewModel.FlipVerticalCommand.Execute(null);
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
