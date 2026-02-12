using System;
using System.Globalization;
using System.Windows.Data;

namespace PodcastVideoEditor.Ui.Converters
{
    /// <summary>
    /// Converter: TrackType (string) → Height (double) for multi-track timeline layout.
    /// Used in TimelineView.xaml ItemsControl template to set row heights.
    /// 
    /// Heights:
    /// - "text" → 48 pixels (text segments)
    /// - "audio" → 48 pixels (audio waveform/segments)
    /// - "visual" → 100 pixels (larger canvas for visual elements)
    /// - default → 48 pixels
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
                "text" => 48.0,
                "audio" => 48.0,
                "visual" => 100.0,
                _ => 48.0
            };
        }
    }
}
