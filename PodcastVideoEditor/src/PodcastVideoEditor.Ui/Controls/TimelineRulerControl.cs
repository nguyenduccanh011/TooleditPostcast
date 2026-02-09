using System;
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
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        LabelTypeface, 10, LabelBrush, dpi);
                    dc.DrawText(ft, new Point(pixelX - 12, 2));
                }
            }
        }

        private static void SelectTickSteps(double pixelsPerSecond, out double minorStep, out double majorStep)
        {
            if (pixelsPerSecond < 3)
            {
                minorStep = 5;
                majorStep = 10;
            }
            else if (pixelsPerSecond < 6)
            {
                minorStep = 2;
                majorStep = 10;
            }
            else if (pixelsPerSecond < 12)
            {
                minorStep = 1;
                majorStep = 5;
            }
            else
            {
                minorStep = 0.5;
                majorStep = 5;
            }
        }

        private static string FormatTimeRuler(double timeSeconds)
        {
            int t = (int)Math.Floor(timeSeconds);
            int m = t / 60;
            int s = t % 60;
            return $"{m}:{s:D2}";
        }
    }
}
