using System.Globalization;
using System.Windows.Data;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Converts time value (seconds) to pixel position on timeline.
    /// This converter requires ConverterParameter to contain PixelsPerSecond value.
    /// Usage: Value = TimeSeconds, ConverterParameter = PixelsPerSecond
    /// </summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class TimeToPixelsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double timeSeconds) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                return timeSeconds * pixelsPerSecond;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double pixels) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                if (pixelsPerSecond > 0)
                    return pixels / pixelsPerSecond;
            }
            return 0.0;
        }
    }

    /// <summary>
    /// Converts segment duration (EndTime - StartTime, in seconds) to pixel width.
    /// Usage: Binding="{Binding EndTime - StartTime}" with ConverterParameter=PixelsPerSecond
    /// For simplicity, we use a composite approach in code-behind instead.
    /// </summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class DurationToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double durationSeconds) &&
                double.TryParse(parameter?.ToString(), out double pixelsPerSecond))
            {
                return Math.Max(30, durationSeconds * pixelsPerSecond); // Minimum 30px width
            }
            return 100.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convert segment duration display (takes two parameters: EndTime, StartTime).
    /// </summary>
    [ValueConversion(typeof(object), typeof(string))]
    public class DurationDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                double.TryParse(values[0]?.ToString(), out double endTime) &&
                double.TryParse(values[1]?.ToString(), out double startTime))
            {
                double durationSeconds = endTime - startTime;
                string sign = durationSeconds < 0 ? "-" : "";
                durationSeconds = Math.Abs(durationSeconds);
                
                int minutes = (int)(durationSeconds / 60);
                double seconds = durationSeconds % 60;
                
                return $"{sign}{minutes}:{seconds:F2}";
            }
            return "0:00.00";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a double to formatted string with specified format in converter parameter.
    /// Usage: Text="{Binding Value, Converter={StaticResource DoubleFormatConverter}, ConverterParameter='F1'}"
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class DoubleFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double dblValue))
            {
                string format = parameter?.ToString() ?? "F2";
                return dblValue.ToString(format, culture);
            }
            return "0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts PixelsPerSecond value to display format "X.Xpx/s".
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class PixelsPerSecondConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double pxPerSec))
            {
                return $"{pxPerSec:F1}px/s";
            }
            return "0.0px/s";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts time value (in seconds) to display format with 1 decimal place.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class TimeValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double timeSeconds))
            {
                return $"{timeSeconds:F1}";
            }
            return "0.0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts transition duration (in seconds) to display format with 3 decimal places.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class TransitionDurationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), out double durationSeconds))
            {
                return $"{durationSeconds:F3}";
            }
            return "0.000";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
