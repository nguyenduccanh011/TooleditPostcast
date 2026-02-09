using System;
using System.Windows;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Controls
{
    /// <summary>
    /// Lightweight waveform renderer (drawn, cached) for timeline audio track.
    /// </summary>
    public sealed class WaveformControl : FrameworkElement
    {
        public static readonly DependencyProperty PeaksProperty =
            DependencyProperty.Register(
                nameof(Peaks),
                typeof(float[]),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender, OnPeaksChanged));

        public static readonly DependencyProperty PixelsPerSecondProperty =
            DependencyProperty.Register(
                nameof(PixelsPerSecond),
                typeof(double),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

        public static readonly DependencyProperty TotalDurationProperty =
            DependencyProperty.Register(
                nameof(TotalDuration),
                typeof(double),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

        public float[] Peaks
        {
            get => (float[])GetValue(PeaksProperty);
            set => SetValue(PeaksProperty, value);
        }

        public double PixelsPerSecond
        {
            get => (double)GetValue(PixelsPerSecondProperty);
            set => SetValue(PixelsPerSecondProperty, value);
        }

        public double TotalDuration
        {
            get => (double)GetValue(TotalDurationProperty);
            set => SetValue(TotalDurationProperty, value);
        }

        private static readonly Brush WaveBrush;

        private DrawingGroup? _cache;
        private float[]? _cachedPeaks;
        private double _cachedWidth;
        private double _cachedHeight;

        static WaveformControl()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x4f, 0xc3, 0xf7));
            brush.Freeze();
            WaveBrush = brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double height = ActualHeight;
            if (height <= 0)
                return;

            double contentWidth = GetContentWidth();
            if (contentWidth <= 0)
                return;

            if (IsCacheInvalid(contentWidth, height))
                RebuildCache(contentWidth, height);

            if (_cache != null)
                dc.DrawDrawing(_cache);
        }

        private static void OnPeaksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WaveformControl control)
            {
                control._cachedPeaks = null;
                control._cache = null;
            }
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WaveformControl control)
            {
                control._cache = null;
            }
        }

        private bool IsCacheInvalid(double width, double height)
        {
            if (_cache == null || _cachedPeaks != Peaks)
                return true;
            if (Math.Abs(_cachedWidth - width) > 0.1 || Math.Abs(_cachedHeight - height) > 0.1)
                return true;
            return false;
        }

        private double GetContentWidth()
        {
            if (PixelsPerSecond <= 0)
                return 0;
            if (TotalDuration <= 0)
                return 0;
            return TotalDuration * PixelsPerSecond;
        }

        private void RebuildCache(double width, double height)
        {
            var peaks = Peaks ?? Array.Empty<float>();
            var group = new DrawingGroup();

            using (var dc = group.Open())
            {
                if (peaks.Length == 0)
                {
                    // nothing to draw
                }
                else
                {
                    int count = peaks.Length;
                    double maxBarHeight = Math.Max(1, height - 4);
                    double barWidth = Math.Max(0.3, (width / count) - 0.2);

                    for (int i = 0; i < count; i++)
                    {
                        float p = peaks[i];
                        if (p <= 0)
                            continue;
                        if (p > 1f)
                            p = 1f;
                        double h = p * maxBarHeight;
                        double x = i * (width / count);
                        var rect = new Rect(x, height - h, barWidth, h);
                        dc.DrawRectangle(WaveBrush, null, rect);
                    }
                }
            }

            group.Freeze();
            _cache = group;
            _cachedPeaks = peaks;
            _cachedWidth = width;
            _cachedHeight = height;
        }
    }
}
