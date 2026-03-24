namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Segment kind constants for multi-track / segment-type plan (TP-004).
/// DB stores string; use these values consistently.
/// </summary>
public static class SegmentKind
{
    /// <summary>Media segment (image/video background). May have BackgroundAssetId and Text.</summary>
    public const string Visual = "visual";

    /// <summary>Script/subtitle text segment. Emphasizes Text; may have no background image.</summary>
    public const string Text = "text";

    /// <summary>Audio segment on an audio track.</summary>
    public const string Audio = "audio";

    /// <summary>Effect segment (visualizer, overlay) on an effect track.</summary>
    public const string Effect = "effect";
}
