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

    // Navigation (not observable; EF/serialization)
    public Project? Project { get; set; }
}
