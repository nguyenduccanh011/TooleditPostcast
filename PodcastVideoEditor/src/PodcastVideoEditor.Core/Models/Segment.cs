using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// A time-based segment within a project (script + background image).
/// Implements INotifyPropertyChanged so UI (e.g. Segment Properties panel) updates when
/// StartTime/EndTime change from drag or resize on the timeline.
/// </summary>
public partial class Segment : ObservableObject
{
    [ObservableProperty]
    private string id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string projectId = string.Empty;

    [ObservableProperty]
    private double startTime;

    [ObservableProperty]
    private double endTime;

    [ObservableProperty]
    private string text = string.Empty;

    [ObservableProperty]
    private string? backgroundAssetId;

    [ObservableProperty]
    private string transitionType = "fade";

    [ObservableProperty]
    private double transitionDuration = 0.5;

    [ObservableProperty]
    private int order;

    /// <summary>
    /// Segment kind: "visual" (media/background), "text" (script), etc. DB column added by AddSegmentKind migration; required for INSERT.
    /// </summary>
    [ObservableProperty]
    private string kind = "visual";

    /// <summary>
    /// Foreign key to the track this segment belongs to.
    /// Multi-track support: each segment is contained in exactly one track.
    /// Nullable during migration; becomes NOT NULL after data migration (ST-2).
    /// </summary>
    [ObservableProperty]
    private string? trackId;

    /// <summary>
    /// Volume level for audio/video segments (0.0 = silent, 1.0 = full volume).
    /// Applies to audio playback when this segment is active.
    /// </summary>
    [ObservableProperty]
    private double volume = 1.0;

    /// <summary>
    /// Fade-in duration in seconds from the segment's start time.
    /// Audio/video volume ramps from 0 to Volume over this duration.
    /// </summary>
    [ObservableProperty]
    private double fadeInDuration;

    /// <summary>
    /// Fade-out duration in seconds before the segment's end time.
    /// Audio/video volume ramps from Volume to 0 over this duration.
    /// </summary>
    [ObservableProperty]
    private double fadeOutDuration;

    /// <summary>
    /// AI-generated keywords for this segment stored as a JSON array string.
    /// Example: "[\"stock market\",\"trading\",\"finance\",\"charts\",\"business\"]"
    /// Populated by the AI analysis pipeline; used to fetch and select background images.
    /// </summary>
    [ObservableProperty]
    private string? keywords;

    /// <summary>
    /// UI-only: waveform peak data for audio segments (not persisted to DB).
    /// Loaded asynchronously by TimelineViewModel after segment is added/loaded.
    /// </summary>
    [ObservableProperty]
    [property: NotMapped]
    private float[]? waveformPeaks;

    /// <summary>
    /// UI-only: whether this segment is part of a multi-segment selection (not persisted).
    /// Set by TimelineViewModel when the user Ctrl/Shift+clicks segments.
    /// </summary>
    [ObservableProperty]
    [property: NotMapped]
    private bool isMultiSelected;

    // Navigation (not observable; EF/serialization)
    public Project? Project { get; set; }

    /// <summary>
    /// Reference to the track this segment belongs to.
    /// Navigation property for EF Core relationship.
    /// </summary>
    public Track? Track { get; set; }

    /// <summary>
    /// Create a detached copy of this segment with all persisted properties.
    /// Navigation properties (Project, Track) and UI-only fields (WaveformPeaks, IsMultiSelected)
    /// are intentionally not copied. Use this for render snapshots and undo/redo state.
    /// </summary>
    public Segment ShallowClone() => new()
    {
        Id                 = Id,
        ProjectId          = ProjectId,
        TrackId            = TrackId,
        Text               = Text,
        Kind               = Kind,
        StartTime          = StartTime,
        EndTime            = EndTime,
        Order              = Order,
        BackgroundAssetId  = BackgroundAssetId,
        Volume             = Volume,
        FadeInDuration     = FadeInDuration,
        FadeOutDuration    = FadeOutDuration,
        TransitionType     = TransitionType,
        TransitionDuration = TransitionDuration,
        Keywords           = Keywords,
    };
}
