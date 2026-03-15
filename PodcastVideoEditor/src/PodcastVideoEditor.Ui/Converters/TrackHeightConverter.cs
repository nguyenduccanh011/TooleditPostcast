using System;
using System.Globalization;
using System.Windows.Data;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Converter: TrackType (string) → icon emoji for the track header button.
    /// text → "T", visual → "🖼", audio → "♪", default → "☰"
    /// </summary>
    public class TrackTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is string t ? t switch
            {
                "text"   => "T",
                "visual" => "🖼",
                "audio"  => "♪",
                _        => "☰"
            } : "☰";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converter: TrackType (string) → Height (double) for multi-track timeline layout.
    /// </summary>
    public class TrackHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string trackType)
            {
                return GetHeight(trackType);
            }

            // Default fallback
            return GetHeight(string.Empty);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("ConvertBack not supported for TrackHeightConverter");
        }

        public static double GetHeight(string trackType)
        {
            return trackType switch
            {
                "text"   => 22.0,
                "audio"  => 48.0,
                "visual" => 48.0,
                _        => 22.0
            };
        }
    }
}
