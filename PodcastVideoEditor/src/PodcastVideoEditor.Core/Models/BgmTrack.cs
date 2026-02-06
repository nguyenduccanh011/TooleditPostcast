namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Background music (BGM) track for a project
/// </summary>
public class BgmTrack
{
    /// <summary>
    /// Unique track ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Project this track belongs to
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = "BGM Track";

    /// <summary>
    /// Path to audio file
    /// </summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Volume level (0-1, default 0.3)
    /// </summary>
    public double Volume { get; set; } = 0.3;

    /// <summary>
    /// Fade-in duration in seconds
    /// </summary>
    public double FadeInSeconds { get; set; } = 2.0;

    /// <summary>
    /// Fade-out duration in seconds
    /// </summary>
    public double FadeOutSeconds { get; set; } = 2.0;

    /// <summary>
    /// Should this track loop?
    /// </summary>
    public bool IsLooping { get; set; } = false;

    /// <summary>
    /// Is this track enabled?
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Start time in project (seconds)
    /// </summary>
    public double StartTime { get; set; } = 0;

    /// <summary>
    /// Display order
    /// </summary>
    public int Order { get; set; }

    // Navigation
    public Project? Project { get; set; }
}
