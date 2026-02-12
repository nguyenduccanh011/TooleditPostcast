namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Represents a timeline track (lane) containing segments of a given type.
/// 
/// A track is an organizational unit in the timeline that holds segments.
/// Multiple tracks can exist simultaneously (text, visual, audio).
/// Display order is determined by the Order property (0 = top = front layer).
/// 
/// Tracks provide:
/// - Type categorization (text, visual, audio)
/// - Collision detection scope (collisions checked only within the same track)
/// - Z-order management for rendering
/// - Lock and visibility controls per track
/// </summary>
public class Track
{
    /// <summary>
    /// Unique identifier for the track.
    /// Generated as GUID string on creation.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Foreign key to the project this track belongs to.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Display order on the timeline.
    /// Order 0 = top of timeline = front/foreground layer.
    /// Higher orders = lower on timeline = background layers.
    /// 
    /// During rendering: render from highest Order → lowest Order (back → front).
    /// For display: row 0 (under ruler) is Order 0; rows increase downward.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Track type categorization.
    /// Valid values: "text", "visual", "audio"
    /// 
    /// - "text": Script/subtitle track (segments with text content)
    /// - "visual": Video/image background track (segments with background images)
    /// - "audio": Audio track (currently supports waveform only, may have BGM clips in future)
    /// 
    /// Recommended: Segment.Kind should match the track's TrackType for consistency.
    /// </summary>
    public string TrackType { get; set; } = "visual";

    /// <summary>
    /// User-visible name for this track.
    /// Examples: "Text 1", "Visual 2", "Audio", "BGM"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Indicates if this track is locked for editing.
    /// When locked, segments in this track cannot be dragged, resized, or modified through UI.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Indicates if this track is visible in the timeline and render output.
    /// When false, the track row is hidden in UI and segments are not rendered.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    // Navigation properties

    /// <summary>
    /// Reference to the project this track belongs to.
    /// </summary>
    public Project? Project { get; set; }

    /// <summary>
    /// Collection of segments belonging to this track.
    /// Each segment in this collection has TrackId = this track's Id.
    /// </summary>
    public ICollection<Segment> Segments { get; set; } = [];
}
