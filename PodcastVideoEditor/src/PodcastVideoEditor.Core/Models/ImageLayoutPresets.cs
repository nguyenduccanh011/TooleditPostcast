namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Well-known values for Track.ImageLayoutPreset.
/// Controls how background images are positioned and sized within the video frame.
/// </summary>
public static class ImageLayoutPresets
{
    /// <summary>Image fills the entire video frame (default). Crops if aspect ratios differ.</summary>
    public const string FullFrame = "FullFrame";

    /// <summary>
    /// 1:1 square image centered horizontally and vertically in the frame.
    /// Width = frame width; height = frame width (square). Top/bottom areas are empty —
    /// ideal for portrait (9:16) projects with title/subtitle overlays above and below.
    /// </summary>
    public const string Square_Center = "Square_Center";

    /// <summary>
    /// 16:9 widescreen image letterboxed and centered in the frame.
    /// Width = frame width; height = frame width × 9/16.
    /// Useful in portrait projects to show a traditional 16:9 clip in the center zone.
    /// </summary>
    public const string Widescreen_Center = "Widescreen_Center";
}
