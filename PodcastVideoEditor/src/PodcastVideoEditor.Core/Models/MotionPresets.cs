namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Well-known values for Segment.MotionPreset and Track auto-motion.
/// Controls Ken Burns-style zoom/pan animation applied to still images during render.
/// </summary>
public static class MotionPresets
{
    /// <summary>No motion effect — static image (default).</summary>
    public const string None = "None";

    /// <summary>Slowly zoom in toward center.</summary>
    public const string ZoomIn = "ZoomIn";

    /// <summary>Start zoomed in, slowly zoom out to reveal full image.</summary>
    public const string ZoomOut = "ZoomOut";

    /// <summary>Slowly pan from right to left.</summary>
    public const string PanLeft = "PanLeft";

    /// <summary>Slowly pan from left to right.</summary>
    public const string PanRight = "PanRight";

    /// <summary>Slowly pan from bottom to top.</summary>
    public const string PanUp = "PanUp";

    /// <summary>Slowly pan from top to bottom.</summary>
    public const string PanDown = "PanDown";

    /// <summary>Zoom in while panning left.</summary>
    public const string ZoomInPanLeft = "ZoomInPanLeft";

    /// <summary>Zoom in while panning right.</summary>
    public const string ZoomInPanRight = "ZoomInPanRight";

    /// <summary>All presets excluding None, used for auto-motion random selection.</summary>
    private static readonly string[] AnimatedPresets =
    [
        ZoomIn, ZoomOut, PanLeft, PanRight, PanUp, PanDown, ZoomInPanLeft, ZoomInPanRight
    ];

    /// <summary>
    /// Get all available animated presets (excluding None).
    /// </summary>
    public static IReadOnlyList<string> GetAllAnimatedPresets() => AnimatedPresets;

    /// <summary>
    /// Get all presets including None.
    /// </summary>
    public static IReadOnlyList<string> GetAllPresets() =>
        [None, .. AnimatedPresets];

    /// <summary>
    /// Get a deterministic random preset based on a seed string (e.g. segment ID).
    /// Uses hash of the seed so the same segment always gets the same preset across renders.
    /// </summary>
    public static string GetRandomPreset(string seed)
    {
        var hash = Math.Abs(seed.GetHashCode(StringComparison.Ordinal));
        return AnimatedPresets[hash % AnimatedPresets.Length];
    }
}
