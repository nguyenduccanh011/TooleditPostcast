using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Interaction logic for CanvasView.xaml
    /// </summary>
    public partial class CanvasView : UserControl
    {
        private CanvasViewModel? _viewModel;
        private Canvas? _mainCanvas;
        private Point _dragStartPoint;
        private bool _isDragging;
        private double _originalX;
        private double _originalY;

        public CanvasView()
        {
            InitializeComponent();
        }

        private void OnCanvasLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as CanvasViewModel;
            _mainCanvas = FindChild<Canvas>(this, "MainCanvas");
            _viewModel?.EnsureVisualizerTimer();
        }

        /// <summary>
        /// Find child element by name in visual tree
        /// </summary>
        private T? FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;

            T? foundChild = null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T typedChild && child is FrameworkElement fe && fe.Name == childName)
                {
                    foundChild = typedChild;
                    break;
                }

                foundChild = FindChild<T>(child, childName);
                if (foundChild != null)
                    break;
            }

            return foundChild;
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
