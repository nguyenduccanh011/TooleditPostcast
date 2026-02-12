using CommunityToolkit.Mvvm.ComponentModel;

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

    // Navigation (not observable; EF/serialization)
    public Project? Project { get; set; }

    /// <summary>
    /// Reference to the track this segment belongs to.
    /// Navigation property for EF Core relationship.
    /// </summary>
    public Track? Track { get; set; }
}
