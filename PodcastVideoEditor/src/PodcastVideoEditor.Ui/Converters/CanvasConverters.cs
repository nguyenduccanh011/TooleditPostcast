using PodcastVideoEditor.Core.Models;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Convert hex color string to Brush.
    /// </summary>
    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hexColor)
            {
                try
                {
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
                }
                catch
                {
                    return new SolidColorBrush(Colors.White);
                }
            }
            return new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush brush)
            {
                return brush.Color.ToString();
            }
            return "#FFFFFF";
        }
    }

    /// <summary>
    /// Convert bool to selection border color.
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isSelected)
            {
                return new SolidColorBrush(isSelected ? Colors.Cyan : Colors.Transparent);
            }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convert Color to Brush.
    /// </summary>
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color color)
            {
                return new SolidColorBrush(color);
            }
            return new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush brush)
            {
                return brush.Color;
            }
            return Colors.White;
        }
    }

    /// <summary>
    /// Convert bool to FontWeight.
    /// </summary>
    public class BoolToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isBold && isBold)
            {
                return FontWeights.Bold;
            }
            return FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FontWeight weight)
            {
                return weight == FontWeights.Bold;
            }
            return false;
        }
    }

    /// <summary>
    /// Convert bool to FontStyle.
    /// </summary>
    public class BoolToFontStyleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isItalic && isItalic)
            {
                return FontStyles.Italic;
            }
            return FontStyles.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FontStyle style)
            {
                return style == FontStyles.Italic;
            }
            return false;
        }
    }

    /// <summary>
    /// Convert Core.Models.TextAlignment to System.Windows.TextAlignment.
    /// </summary>
    public class AlignmentToTextAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Core.Models.TextAlignment alignment)
            {
                return alignment switch
                {
                    Core.Models.TextAlignment.Left => System.Windows.TextAlignment.Left,
                    Core.Models.TextAlignment.Center => System.Windows.TextAlignment.Center,
                    Core.Models.TextAlignment.Right => System.Windows.TextAlignment.Right,
                    _ => System.Windows.TextAlignment.Center
                };
            }
            return System.Windows.TextAlignment.Center;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Windows.TextAlignment alignment)
            {
                return alignment switch
                {
                    System.Windows.TextAlignment.Left => Core.Models.TextAlignment.Left,
                    System.Windows.TextAlignment.Center => Core.Models.TextAlignment.Center,
                    System.Windows.TextAlignment.Right => Core.Models.TextAlignment.Right,
                    _ => Core.Models.TextAlignment.Center
                };
            }
            return Core.Models.TextAlignment.Center;
        }
    }

    /// <summary>
    /// Convert TextAlignment enum to HorizontalAlignment.
    /// </summary>
    public class TextAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Core.Models.TextAlignment alignment)
            {
                return alignment switch
                {
                    Core.Models.TextAlignment.Left => HorizontalAlignment.Left,
                    Core.Models.TextAlignment.Center => HorizontalAlignment.Center,
                    Core.Models.TextAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center
                };
            }
            return HorizontalAlignment.Center;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HorizontalAlignment alignment)
            {
                return alignment switch
                {
                    HorizontalAlignment.Left => Core.Models.TextAlignment.Left,
                    HorizontalAlignment.Center => Core.Models.TextAlignment.Center,
                    HorizontalAlignment.Right => Core.Models.TextAlignment.Right,
                    _ => Core.Models.TextAlignment.Center
                };
            }
            return Core.Models.TextAlignment.Center;
        }
    }

    /// <summary>
    /// Convert ScaleMode enum to Stretch.
    /// </summary>
    public class ScaleModeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ScaleMode scaleMode)
            {
                return scaleMode switch
                {
                    ScaleMode.Fit => Stretch.Uniform,
                    ScaleMode.Fill => Stretch.UniformToFill,
                    ScaleMode.Stretch => Stretch.Fill,
                    _ => Stretch.Uniform
                };
            }
            return Stretch.Uniform;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Stretch stretch)
            {
                return stretch switch
                {
                    Stretch.Uniform => ScaleMode.Fit,
                    Stretch.UniformToFill => ScaleMode.Fill,
                    Stretch.Fill => ScaleMode.Stretch,
                    _ => ScaleMode.Fit
                };
            }
            return ScaleMode.Fit;
        }
    }

    /// <summary>
    /// Convert object to Visibility: null = Collapsed, non-null = Visible.
    /// </summary>
    public class ObjectToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convert object to Visibility: null = Visible, non-null = Collapsed.
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convert bool to Visibility.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool visible)
            {
                return visible ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible;
            }
            return true;
        }
    }

    /// <summary>
    /// Convert bool to Visibility (inverted).
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool visible)
            {
                return visible ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility != Visibility.Visible;
            }
            return false;
        }
    }
}
