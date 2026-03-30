using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Controls
{
    /// <summary>
    /// Lightweight ruler renderer for the timeline (drawn, not element-based).
    /// </summary>
    public sealed class TimelineRulerControl : FrameworkElement
    {
        public static readonly DependencyProperty PixelsPerSecondProperty =
            DependencyProperty.Register(
                nameof(PixelsPerSecond),
                typeof(double),
                typeof(TimelineRulerControl),
                new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TotalDurationProperty =
            DependencyProperty.Register(
                nameof(TotalDuration),
                typeof(double),
                typeof(TimelineRulerControl),
                new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ExtraSecondsProperty =
            DependencyProperty.Register(
                nameof(ExtraSeconds),
                typeof(double),
                typeof(TimelineRulerControl),
                new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

        public double ExtraSeconds
        {
            get => (double)GetValue(ExtraSecondsProperty);
            set => SetValue(ExtraSecondsProperty, value);
        }

        private static readonly Pen MediumPen;
        private static readonly Pen SmallPen;
        private static readonly Brush LabelBrush;
        private static readonly Typeface LabelTypeface;

        // Cache FormattedText objects to avoid GC pressure during rapid OnRender calls
        private readonly Dictionary<string, FormattedText> _formattedTextCache = new();
        private double _cachedDpi;

        static TimelineRulerControl()
        {
            var mediumBrush = new SolidColorBrush(Color.FromRgb(0xb0, 0xbb, 0xc5));
            mediumBrush.Freeze();
            var smallBrush = new SolidColorBrush(Color.FromRgb(0x75, 0x80, 0x8b));
            smallBrush.Freeze();
            var labelBrush = new SolidColorBrush(Color.FromRgb(0xa0, 0xa0, 0xa0));
            labelBrush.Freeze();

            MediumPen = new Pen(mediumBrush, 1.2);
            MediumPen.Freeze();
            SmallPen = new Pen(smallBrush, 0.8);
            SmallPen.Freeze();
            LabelBrush = labelBrush;
            LabelTypeface = new Typeface("Segoe UI");
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0 || PixelsPerSecond <= 0)
                return;

            double displayDuration = Math.Max(0, TotalDuration) + Math.Max(0, ExtraSeconds);
            if (displayDuration <= 0)
                return;

            SelectTickSteps(PixelsPerSecond, out double minorStep, out double majorStep);

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Invalidate FormattedText cache if DPI changed
            if (Math.Abs(dpi - _cachedDpi) > 0.001)
            {
                _formattedTextCache.Clear();
                _cachedDpi = dpi;
            }

            for (double timeSeconds = 0; timeSeconds <= displayDuration; timeSeconds += minorStep)
            {
                double pixelX = timeSeconds * PixelsPerSecond;
                if (pixelX > width + 50)
                    break;

                bool isMajor = Math.Abs(timeSeconds % majorStep) < 0.0001;
                double y1 = isMajor ? 22 : 28;
                var pen = isMajor ? MediumPen : SmallPen;

                dc.DrawLine(pen, new Point(pixelX, y1), new Point(pixelX, 35));

                if (isMajor)
                {
                    string label = FormatTimeRuler(timeSeconds);
                    if (!_formattedTextCache.TryGetValue(label, out var ft))
                    {
                        ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            LabelTypeface, 10, LabelBrush, dpi);
                        _formattedTextCache[label] = ft;
                    }
                    dc.DrawText(ft, new Point(pixelX - 12, 2));
                }
            }
        }

        private static void SelectTickSteps(double pixelsPerSecond, out double minorStep, out double majorStep)
        {
            // Adaptive tick table: each entry is (ppsThreshold, minor, major).
            // At higher PPS (zoomed in) we show finer ticks; at lower PPS we show coarser ticks.
            // Goal: major tick labels stay roughly 80-150 px apart.
            if (pixelsPerSecond >= 200)       { minorStep = 0.1;  majorStep = 0.5; }
            else if (pixelsPerSecond >= 80)   { minorStep = 0.25; majorStep = 1; }
            else if (pixelsPerSecond >= 40)   { minorStep = 0.5;  majorStep = 2; }
            else if (pixelsPerSecond >= 15)   { minorStep = 1;    majorStep = 5; }
            else if (pixelsPerSecond >= 6)    { minorStep = 2;    majorStep = 10; }
            else if (pixelsPerSecond >= 3)    { minorStep = 5;    majorStep = 15; }
            else if (pixelsPerSecond >= 1.5)  { minorStep = 5;    majorStep = 30; }
            else                               { minorStep = 10;   majorStep = 60; }
        }

        private static string FormatTimeRuler(double timeSeconds)
        {
            if (timeSeconds < 60)
            {
                // Sub-second precision when we have fractional values
                double frac = timeSeconds - Math.Floor(timeSeconds);
                if (frac > 0.001)
                    return $"0:{timeSeconds:00.0}";
                return $"0:{(int)timeSeconds:D2}";
            }
            int t = (int)Math.Floor(timeSeconds);
            int m = t / 60;
            int s = t % 60;
            double sub = timeSeconds - t;
            if (sub > 0.001)
                return $"{m}:{s:D2}.{(int)(sub * 10)}";
            return $"{m}:{s:D2}";
        }
    }
}
