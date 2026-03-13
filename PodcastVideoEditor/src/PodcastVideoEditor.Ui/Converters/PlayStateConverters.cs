using System;
using System.Globalization;
using System.Windows.Data;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Converts IsPlaying boolean to play/pause icon symbol (one-way only).
    /// Returns "⏸" when playing (show pause icon), "▶" when stopped.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(string))]
    public class PlayStateToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isPlaying)
            {
                return isPlaying ? "⏸" : "▶";  // Pause icon if playing, Play icon if stopped
            }
            return "▶";  // Default to play icon
        }

        /// <summary>
        /// One-way converter only - back conversion not supported.
        /// </summary>
        public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;  // Not supported - one-way converter
        }
    }

    /// <summary>
    /// Converts IsPlaying boolean to tooltip text for play/pause button (one-way only).
    /// </summary>
    [ValueConversion(typeof(bool), typeof(string))]
    public class PlayStateToTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isPlaying)
            {
                return isPlaying ? "Pause (Space)" : "Play (Space)";
            }
            return "Play (Space)";  // Default to play tooltip
        }

        /// <summary>
        /// One-way converter only - back conversion not supported.
        /// </summary>
        public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;  // Not supported - one-way converter
        }
    }
}
