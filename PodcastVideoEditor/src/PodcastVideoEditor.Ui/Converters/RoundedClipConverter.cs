using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Converters;

/// <summary>
/// Converts ActualWidth + ActualHeight into a RectangleGeometry with rounded corners,
/// used as UIElement.Clip to enforce rounded-corner clipping on Border children.
/// </summary>
public class RoundedClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double width && values[1] is double height && width > 0 && height > 0)
        {
            double radius = 6;
            if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                radius = r;
            var geometry = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
            geometry.Freeze(); // Perf: frozen Freezable skips change-tracking overhead
            return geometry;
        }
        return DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
