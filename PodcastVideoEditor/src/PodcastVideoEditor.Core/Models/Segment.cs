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
    [NotifyPropertyChangedFor(nameof(SegmentDisplayDuration))]
    private double startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentDisplayDuration))]
    private double endTime;

    /// <summary>
    /// Computed: segment's visible duration on the timeline (EndTime - StartTime).
    /// Used by WaveformControl to compute the correct peak bin slice.
    /// </summary>
    [NotMapped]
    public double SegmentDisplayDuration => Math.Max(0, EndTime - StartTime);

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
    /// AI-generated keywords for this segment, stored as a JSON array string.
    /// </summary>
    [ObservableProperty]
    private string? keywords;

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
    /// Offset into the source audio file where playback begins (in seconds).
    /// For example, if SourceStartOffset = 5.0, the segment plays from 5s into the audio file.
    /// Default 0 = play from the beginning of the source file.
    /// </summary>
    [ObservableProperty]
    private double sourceStartOffset;

    /// <summary>
    /// Ken Burns motion effect preset for this image segment.
    /// Valid values defined in <see cref="MotionPresets"/>.
    /// "None" = static (default). Other values: ZoomIn, ZoomOut, PanLeft, PanRight, etc.
    /// When set to "None" and the parent track has AutoMotionEnabled, a random preset is
    /// assigned at render time (deterministic based on segment Id).
    /// Only applies to still images; video segments ignore this property.
    /// </summary>
    [ObservableProperty]
    private string motionPreset = MotionPresets.None;

    /// <summary>
    /// Intensity of the motion effect (0.0 = subtle, 1.0 = dramatic).
    /// Controls the magnitude of zoom factor or pan distance.
    /// Default 0.3 provides a gentle, professional Ken Burns effect.
    /// When null, falls back to the parent track's MotionIntensity.
    /// </summary>
    [ObservableProperty]
    private double? motionIntensity;

    /// <summary>
    /// UI-only: waveform peak data for audio segments (not persisted to DB).
    /// Loaded asynchronously by TimelineViewModel after segment is added/loaded.
    /// </summary>
    [ObservableProperty]
    [property: NotMapped]
    private float[]? waveformPeaks;

    /// <summary>
    /// UI-only: total duration of the source audio file in seconds (not persisted).
    /// Used by WaveformControl to compute visible slice when SourceStartOffset > 0.
    /// Set alongside WaveformPeaks by TimelineViewModel.
    /// </summary>
    [ObservableProperty]
    [property: NotMapped]
    private double sourceFileDuration;

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
        Keywords           = Keywords,
        StartTime          = StartTime,
        EndTime            = EndTime,
        Order              = Order,
        BackgroundAssetId  = BackgroundAssetId,
        Volume             = Volume,
        FadeInDuration     = FadeInDuration,
        FadeOutDuration    = FadeOutDuration,
        SourceStartOffset  = SourceStartOffset,
        TransitionType     = TransitionType,
        TransitionDuration = TransitionDuration,
        MotionPreset       = MotionPreset,
        MotionIntensity    = MotionIntensity,
    };
}
