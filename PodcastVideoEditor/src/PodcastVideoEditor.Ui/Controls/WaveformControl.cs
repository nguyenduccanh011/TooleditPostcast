using System;
using System.Windows;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Controls
{
    /// <summary>
    /// Lightweight waveform renderer for timeline audio segments.
    /// Renders peaks into the control's own ActualWidth (segment-local),
    /// so changing timeline duration does NOT affect other segments' waveforms.
    /// Supports SourceStartOffset / SourceDuration / SegmentDisplayDuration
    /// to display the correct slice of peaks for trimmed segments.
    /// </summary>
    public sealed class WaveformControl : FrameworkElement
    {
        public static readonly DependencyProperty PeaksProperty =
            DependencyProperty.Register(
                nameof(Peaks),
                typeof(object),  // Must be object: float[] typed DP causes MC4102 in DataTemplate bindings
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender, OnPeaksChanged));

        /// <summary>
        /// Offset into the source audio file (seconds). Only the portion starting at this offset is rendered.
        /// Default 0 = render from the beginning of the peaks array.
        /// </summary>
        public static readonly DependencyProperty SourceStartOffsetProperty =
            DependencyProperty.Register(
                nameof(SourceStartOffset),
                typeof(double),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

        /// <summary>
        /// Duration of the source audio file (seconds). Used with SourceStartOffset to compute the visible slice.
        /// Default 0 = use all peaks (no slicing).
        /// </summary>
        public static readonly DependencyProperty SourceDurationProperty =
            DependencyProperty.Register(
                nameof(SourceDuration),
                typeof(double),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

        /// <summary>
        /// The segment's visible duration on the timeline (seconds), i.e. EndTime - StartTime.
        /// Used to compute the correct endBin when the segment is shorter than the full source audio.
        /// Default 0 = show all peaks from startBin onwards.
        /// </summary>
        public static readonly DependencyProperty SegmentDisplayDurationProperty =
            DependencyProperty.Register(
                nameof(SegmentDisplayDuration),
                typeof(double),
                typeof(WaveformControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

        /// <remarks>Typed as <c>object</c> for WPF XAML DataTemplate compat (see MC4102). Only float[] values are meaningful.</remarks>
        public object Peaks
        {
            get => GetValue(PeaksProperty);
            set => SetValue(PeaksProperty, value);
        }

        public double SourceStartOffset
        {
            get => (double)GetValue(SourceStartOffsetProperty);
            set => SetValue(SourceStartOffsetProperty, value);
        }

        public double SourceDuration
        {
            get => (double)GetValue(SourceDurationProperty);
            set => SetValue(SourceDurationProperty, value);
        }

        public double SegmentDisplayDuration
        {
            get => (double)GetValue(SegmentDisplayDurationProperty);
            set => SetValue(SegmentDisplayDurationProperty, value);
        }

        private static readonly Brush WaveBrush;

        private DrawingGroup? _cache;
        private float[]? _cachedPeaks;
        private double _cachedWidth;
        private double _cachedHeight;
        private double _cachedSourceOffset;
        private double _cachedSourceDuration;
        private double _cachedSegmentDuration;

        static WaveformControl()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x4f, 0xc3, 0xf7));
            brush.Freeze();
            WaveBrush = brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            if (IsCacheInvalid(width, height))
                RebuildCache(width, height);

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

        private float[] GetPeaksArray() => Peaks as float[] ?? Array.Empty<float>();

        private bool IsCacheInvalid(double width, double height)
        {
            if (_cache == null || _cachedPeaks != GetPeaksArray())
                return true;
            if (Math.Abs(_cachedWidth - width) > 0.1 || Math.Abs(_cachedHeight - height) > 0.1)
                return true;
            if (Math.Abs(_cachedSourceOffset - SourceStartOffset) > 0.001
                || Math.Abs(_cachedSourceDuration - SourceDuration) > 0.001
                || Math.Abs(_cachedSegmentDuration - SegmentDisplayDuration) > 0.001)
                return true;
            return false;
        }

        private void RebuildCache(double width, double height)
        {
            var peaks = GetPeaksArray();
            var group = new DrawingGroup();

            using (var dc = group.Open())
            {
                if (peaks.Length > 0)
                {
                    // Compute visible slice of peaks:
                    // - startBin derived from SourceStartOffset
                    // - endBin derived from SourceStartOffset + SegmentDisplayDuration
                    // Both are relative to SourceDuration (the full source audio length).
                    int startBin = 0;
                    int endBin = peaks.Length;
                    double srcDur = SourceDuration;
                    double srcOffset = SourceStartOffset;
                    double segDur = SegmentDisplayDuration;

                    if (srcDur > 0)
                    {
                        double ratio = (double)peaks.Length / srcDur;
                        if (srcOffset > 0)
                            startBin = Math.Clamp((int)(srcOffset * ratio), 0, peaks.Length);
                        if (segDur > 0)
                            endBin = Math.Clamp((int)((srcOffset + segDur) * ratio), startBin, peaks.Length);
                    }

                    if (startBin >= endBin)
                    {
                        startBin = 0;
                        endBin = peaks.Length;
                    }

                    int count = endBin - startBin;
                    if (count <= 0) count = 1;

                    double maxBarHeight = Math.Max(1, height - 4);
                    double slotWidth = width / count;
                    double barWidth = Math.Max(0.3, slotWidth - 0.2);

                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        for (int i = 0; i < count; i++)
                        {
                            float p = peaks[startBin + i];
                            if (p <= 0)
                                continue;
                            if (p > 1f)
                                p = 1f;
                            double h = p * maxBarHeight;
                            double x = i * slotWidth;
                            ctx.BeginFigure(new Point(x, height), true, true);
                            ctx.LineTo(new Point(x + barWidth, height), false, false);
                            ctx.LineTo(new Point(x + barWidth, height - h), false, false);
                            ctx.LineTo(new Point(x, height - h), false, false);
                        }
                    }
                    geo.Freeze();
                    dc.DrawGeometry(WaveBrush, null, geo);
                }
            }

            group.Freeze();
            _cache = group;
            _cachedPeaks = peaks;
            _cachedWidth = width;
            _cachedHeight = height;
            _cachedSourceOffset = SourceStartOffset;
            _cachedSourceDuration = SourceDuration;
            _cachedSegmentDuration = SegmentDisplayDuration;
        }
    }
}
