using PodcastVideoEditor.Ui.ViewModels;
using SkiaSharp.Views.Desktop;
using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// VisualizerView - displays real-time spectrum visualization.
    /// </summary>
    public partial class VisualizerView : UserControl
    {
        private VisualizerViewModel? _viewModel;
        private readonly DispatcherTimer _renderTimer;

        public VisualizerView()
        {
            InitializeComponent();
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30fps for smoother UI
            };
            _renderTimer.Tick += RenderTimer_Tick;
            Unloaded += OnViewUnloaded;
        }

        private void OnVisualizerLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewModel = DataContext as VisualizerViewModel;
            if (_viewModel != null)
            {
                VisualizerCanvas.SizeChanged += OnCanvasSizeChanged;
                TryInitializeWithActualSize();
                _renderTimer.Start();
            }
        }

        private void OnCanvasSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            TryInitializeWithActualSize();
        }

        private void TryInitializeWithActualSize()
        {
            if (_viewModel == null)
                return;

            var width = (int)VisualizerCanvas.ActualWidth;
            var height = (int)VisualizerCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            _viewModel.VisualizerWidth = width;
            _viewModel.VisualizerHeight = height;
            _viewModel.Initialize();
        }

        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            if (_viewModel == null)
                return;

            VisualizerCanvas.InvalidateVisual();
        }

        private void OnViewUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _renderTimer.Stop();
        }

        private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            if (_viewModel == null)
                return;

            var canvas = e.Surface.Canvas;
            canvas.Clear(SkiaSharp.SKColors.Black);

            // Get current frame from visualizer
            var bitmap = _viewModel?.GetCurrentFrame();
            if (bitmap != null)
            {
                // Draw the bitmap scaled to fit the surface
                var destRect = new SkiaSharp.SKRect(0, 0, e.Info.Width, e.Info.Height);
                canvas.DrawBitmap(bitmap, destRect);
            }
        }
    }
}
