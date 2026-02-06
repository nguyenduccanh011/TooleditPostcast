namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// A time-based segment within a project (script + background image)
/// </summary>
public class Segment
{
    /// <summary>
    /// Unique segment ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Project this segment belongs to
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Start time in seconds
    /// </summary>
    public double StartTime { get; set; }

    /// <summary>
    /// End time in seconds
    /// </summary>
    public double EndTime { get; set; }

    /// <summary>
    /// Script text to display during this segment
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// ID of background asset (image or video)
    /// </summary>
    public string? BackgroundAssetId { get; set; }

    /// <summary>
    /// Type of transition to next segment (fade, slide, wipe, flip, none)
    /// </summary>
    public string TransitionType { get; set; } = "fade";

    /// <summary>
    /// Transition duration in seconds
    /// </summary>
    public double TransitionDuration { get; set; } = 0.5;

    /// <summary>
    /// Display order within project
    /// </summary>
    public int Order { get; set; }

    // Navigation
    public Project? Project { get; set; }
}
