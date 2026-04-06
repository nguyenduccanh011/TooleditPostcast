using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

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
public partial class Track : ObservableObject
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
    [ObservableProperty]
    private int order;

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
    /// Semantic role used for template mapping and automation.
    /// See <see cref="TrackRoles"/> constants for supported values.
    /// </summary>
    [ObservableProperty]
    private string trackRole = TrackRoles.Unspecified;

    /// <summary>
    /// Duration policy for this track.
    /// See <see cref="TrackSpanModes"/> constants for supported values.
    /// </summary>
    [ObservableProperty]
    private string spanMode = TrackSpanModes.SegmentBound;

    /// <summary>
    /// Indicates if this track is locked for editing.
    /// When locked, segments in this track cannot be dragged, resized, or modified through UI.
    /// </summary>
    [ObservableProperty]
    private bool isLocked;

    /// <summary>
    /// Indicates if this track is visible in the timeline and render output.
    /// When false, the track row is hidden in UI and segments are not rendered.
    /// </summary>
    [ObservableProperty]
    private bool isVisible = true;

    /// <summary>
    /// Controls how background images are laid out within the video frame for all segments in this track.
    /// Valid values defined in <see cref="ImageLayoutPresets"/>:
    ///   "FullFrame"         — image fills the entire frame (default)
    ///   "Square_Center"     — 1:1 centered, leaving top/bottom space for titles/subs
    ///   "Widescreen_Center" — 16:9 letterboxed and centered in a portrait frame
    /// Changing this value automatically affects render output for all segments in this track.
    /// </summary>
    [ObservableProperty]
    private string imageLayoutPreset = ImageLayoutPresets.FullFrame;

    /// <summary>
    /// When enabled, all image segments in this track that have MotionPreset == "None"
    /// will automatically receive a random Ken Burns motion effect at render time.
    /// The random selection is deterministic (based on segment Id) so re-renders produce
    /// identical results. Only applies to visual tracks with still image segments.
    /// </summary>
    [ObservableProperty]
    private bool autoMotionEnabled;

    /// <summary>
    /// Default motion intensity for auto-assigned effects on this track (0.0–1.0).
    /// Used as fallback when a segment's MotionIntensity is null.
    /// Default 0.3 provides a gentle, professional Ken Burns effect.
    /// </summary>
    [ObservableProperty]
    private double motionIntensity = 0.3;

    /// <summary>
    /// Default overlay color (hex) applied on top of images in this track.
    /// Used as fallback when a segment's OverlayColorHex is null.
    /// Typical use: "#000000" (black) to darken images for text readability.
    /// </summary>
    [ObservableProperty]
    private string overlayColorHex = "#000000";

    /// <summary>
    /// Default overlay opacity for all segments in this track (0.0–1.0).
    /// 0.0 = no overlay (disabled), 1.0 = fully opaque overlay.
    /// Used as fallback when a segment's OverlayOpacity is null.
    /// </summary>
    [ObservableProperty]
    private double overlayOpacity;

    /// <summary>
    /// JSON-serialized shared text style template for text tracks.
    /// When set, all TextOverlayElements on this track share the same visual style.
    /// Only used when TrackType == "text". Null for visual/audio tracks.
    /// </summary>
    public string? TextStyleJson { get; set; }

    /// <summary>
    /// Deserialized accessor for <see cref="TextStyleJson"/>.
    /// Returns null if TextStyleJson is empty or invalid.
    /// </summary>
    [NotMapped]
    public TextStyleTemplate? TextStyle
    {
        get => TextStyleTemplate.FromJson(TextStyleJson);
        set => TextStyleJson = value?.ToJson();
    }

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

    /// <summary>
    /// Create a detached copy of this track with all persisted properties.
    /// Navigation properties (Project) and Segments are intentionally not copied.
    /// Use this for render snapshots to avoid missing new Track properties.
    /// </summary>
    public Track ShallowClone() => new()
    {
        Id                 = Id,
        ProjectId          = ProjectId,
        Name               = Name,
        TrackType          = TrackType,
        TrackRole          = TrackRole,
        SpanMode           = SpanMode,
        Order              = Order,
        IsVisible          = IsVisible,
        IsLocked           = IsLocked,
        ImageLayoutPreset  = ImageLayoutPreset,
        AutoMotionEnabled  = AutoMotionEnabled,
        MotionIntensity    = MotionIntensity,
        OverlayColorHex    = OverlayColorHex,
        OverlayOpacity     = OverlayOpacity,
        TextStyleJson      = TextStyleJson,
    };
}
