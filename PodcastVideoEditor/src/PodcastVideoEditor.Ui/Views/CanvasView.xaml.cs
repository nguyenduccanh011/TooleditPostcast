using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
            
            _viewModel?.EnsureVisualizerTimer();
            
            // Sync VideoSource if already set before Loaded fires
            if (_viewModel?.VideoSource != null && _videoPreview != null)
                SetVideoSource(_viewModel.VideoSource);
        }

        private void OnCanvasUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
        /// </summary>
        private void OnCanvasElementMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_mainCanvas == null) return;

            if (sender is Border border && border.DataContext is CanvasElement element)
            {
                _viewModel?.SelectElement(element);
                
                _dragStartPoint = e.GetPosition(_mainCanvas);
                _originalX = element.X;
                _originalY = element.Y;
                _isDragging = true;

                e.Handled = true;

                // Start drag
                border.MouseMove += OnCanvasElementMouseMove;
                border.MouseUp += OnCanvasElementMouseUp;
                border.CaptureMouse();
            }
        }

        /// <summary>
        /// Handle mouse move for dragging elements.
        /// </summary>
        private void OnCanvasElementMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _viewModel?.SelectedElement == null || _mainCanvas == null)
                return;

            if (sender is Border border)
            {
                var currentPoint = e.GetPosition(_mainCanvas);
                var offsetX = currentPoint.X - _dragStartPoint.X;
                var offsetY = currentPoint.Y - _dragStartPoint.Y;

                _viewModel.SelectedElement.X = _originalX + offsetX;
                _viewModel.SelectedElement.Y = _originalY + offsetY;
            }
        }

        /// <summary>
        /// Handle mouse up to stop dragging.
        /// </summary>
        private void OnCanvasElementMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;

            if (sender is Border border)
            {
                border.MouseMove -= OnCanvasElementMouseMove;
                border.MouseUp -= OnCanvasElementMouseUp;
                border.ReleaseMouseCapture();
            }
        }

        /// <summary>
        /// Handle keyboard shortcuts.
        /// </summary>
        private void OnCanvasKeyDown(object sender, KeyEventArgs e)
        {
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

                // Arrow keys for fine positioning
                case Key.Left:
                    _viewModel.SelectedElement.X -= (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    e.Handled = true;
                    break;

                case Key.Right:
                    _viewModel.SelectedElement.X += (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    e.Handled = true;
                    break;

                case Key.Up:
                    _viewModel.SelectedElement.Y -= (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    e.Handled = true;
                    break;

                case Key.Down:
                    _viewModel.SelectedElement.Y += (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                    e.Handled = true;
                    break;
            }
        }
    }
}
