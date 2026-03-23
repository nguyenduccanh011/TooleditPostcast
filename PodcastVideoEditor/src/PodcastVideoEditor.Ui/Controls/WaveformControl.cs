using System;
using System.Windows;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Controls
{
    /// <summary>
    /// Lightweight waveform renderer for timeline audio segments.
    /// Uses global-coordinate rendering: each pixel column maps to a fixed time
    /// range determined by PixelsPerSecond, so resizing a segment only changes how
    /// many columns are drawn — never the mapping from time to pixel position.
    /// This matches commercial DAW behaviour (CapCut, DaVinci, Audacity).
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

        /// <summary>
        /// Timeline pixels-per-second at the current zoom level. Used for global-coordinate
        /// rendering: each pixel column covers exactly 1/PixelsPerSecond seconds of audio.
        /// Default 0 = fall back to segment-local (legacy) mapping.
        /// </summary>
        public static readonly DependencyProperty PixelsPerSecondProperty =
            DependencyProperty.Register(
                nameof(PixelsPerSecond),
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

        public double PixelsPerSecond
        {
            get => (double)GetValue(PixelsPerSecondProperty);
            set => SetValue(PixelsPerSecondProperty, value);
        }

        private static readonly Brush WaveBrush;

        private DrawingGroup? _cache;
        private float[]? _cachedPeaks;
        private double _cachedWidth;
        private double _cachedHeight;
        private double _cachedSourceOffset;
        private double _cachedSourceDuration;
        private double _cachedSegmentDuration;
        private double _cachedPps;

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
                || Math.Abs(_cachedSegmentDuration - SegmentDisplayDuration) > 0.001
                || Math.Abs(_cachedPps - PixelsPerSecond) > 0.001)
                return true;
            return false;
        }

        /// <summary>
        /// Global-coordinate rendering: for each pixel column, compute the time range it
        /// represents via PixelsPerSecond, then find the max peak in that range.
        /// This ensures the bar↔time mapping is constant regardless of segment width.
        /// </summary>
        private void RebuildCache(double width, double height)
        {
            var peaks = GetPeaksArray();
            var group = new DrawingGroup();

            using (var dc = group.Open())
            {
                if (peaks.Length > 0)
                {
                    double srcDur = SourceDuration;
                    double srcOffset = SourceStartOffset;
                    double pps = PixelsPerSecond;

                    // Bins-per-second ratio: maps time → peak array index
                    double binsPerSecond = srcDur > 0 ? peaks.Length / srcDur : peaks.Length;

                    // ── 1. For each pixel column, compute the time window it covers ─
                    // Global-coordinate: pixel x covers [srcOffset + x/pps, srcOffset + (x+1)/pps)
                    // This mapping is CONSTANT — resizing the segment only changes the
                    // number of columns, not which time each column represents.
                    int barCount = Math.Max(1, (int)Math.Ceiling(width));
                    var displayPeaks = new float[barCount];

                    // Seconds-per-pixel: how much audio time each pixel column represents
                    double secPerPixel = pps > 0 ? 1.0 / pps : 0;

                    // Fallback: if PPS is not set, derive from segment duration (legacy)
                    if (secPerPixel <= 0)
                    {
                        double segDur = SegmentDisplayDuration;
                        if (segDur <= 0) segDur = srcDur > 0 ? srcDur : 1.0;
                        secPerPixel = segDur / width;
                    }

                    for (int col = 0; col < barCount; col++)
                    {
                        // Time range this pixel covers (global-coordinate)
                        double t0 = srcOffset + col * secPerPixel;
                        double t1 = srcOffset + (col + 1) * secPerPixel;

                        // Map to bin indices using floating-point, round at boundaries
                        int binStart = Math.Clamp((int)Math.Floor(t0 * binsPerSecond), 0, peaks.Length);
                        int binEnd = Math.Clamp((int)Math.Ceiling(t1 * binsPerSecond), binStart, peaks.Length);
                        if (binEnd <= binStart) binEnd = Math.Min(binStart + 1, peaks.Length);

                        float maxVal = 0;
                        for (int j = binStart; j < binEnd; j++)
                        {
                            if (peaks[j] > maxVal) maxVal = peaks[j];
                        }
                        displayPeaks[col] = maxVal;
                    }

                    // ── 2. Render bars ──────────────────────────────────────────────
                    double maxBarHeight = Math.Max(1, height - 4);
                    double slotWidth = width / barCount;
                    double barWidth = Math.Max(0.3, slotWidth - 0.2);

                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        for (int i = 0; i < barCount; i++)
                        {
                            float p = displayPeaks[i];
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
            _cachedPps = PixelsPerSecond;
        }
    }
}
