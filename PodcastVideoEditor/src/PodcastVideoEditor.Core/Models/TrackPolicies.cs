namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Stable role identifiers for timeline tracks.
/// Roles drive template mapping, AI routing, and UX semantics.
/// </summary>
public static class TrackRoles
{
    public const string Unspecified = "unspecified";
    public const string BrandOverlay = "brand_overlay";
    public const string TitleOverlay = "title_overlay";
    public const string ScriptText = "script_text";
    public const string AiContent = "ai_content";
    public const string Visualizer = "visualizer";
    public const string BackgroundContent = "background_content";
}

/// <summary>
/// Duration/span policy for track segments.
/// </summary>
public static class TrackSpanModes
{
    /// <summary>
    /// Track follows the project's effective max end time.
    /// </summary>
    public const string ProjectDuration = "project_duration";

    /// <summary>
    /// Track preserves template-defined timing.
    /// </summary>
    public const string TemplateDuration = "template_duration";

    /// <summary>
    /// Segment-level timing is authoritative.
    /// </summary>
    public const string SegmentBound = "segment_bound";

    /// <summary>
    /// User manually controls timing, no auto stretching.
    /// </summary>
    public const string Manual = "manual";
}
