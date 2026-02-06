namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Main project entity - represents a video editing project
/// </summary>
public class Project
{
    /// <summary>
    /// Unique project ID (GUID)
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-friendly project name
    /// </summary>
    public string Name { get; set; } = "Untitled Project";

    /// <summary>
    /// Path to the audio file associated with this project
    /// </summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Description or notes about the project
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When the project was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the project was last modified
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Project render settings (resolution, quality, etc.)
    /// </summary>
    public RenderSettings RenderSettings { get; set; } = new();

    // Navigation properties
    public ICollection<Segment> Segments { get; set; } = [];
    public ICollection<Element> Elements { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
    public ICollection<BgmTrack> BgmTracks { get; set; } = [];
}

/// <summary>
/// Render configuration for a project
/// </summary>
public class RenderSettings
{
    /// <summary>
    /// Output resolution width (e.g., 1080, 720, 480)
    /// </summary>
    public int ResolutionWidth { get; set; } = 1080;

    /// <summary>
    /// Output resolution height (e.g., 1920, 1280, 854)
    /// </summary>
    public int ResolutionHeight { get; set; } = 1920;

    /// <summary>
    /// Aspect ratio (e.g., "9:16", "16:9", "1:1", "4:5")
    /// </summary>
    public string AspectRatio { get; set; } = "9:16";

    /// <summary>
    /// Quality level: Low, Medium, High (affects CRF value)
    /// </summary>
    public string Quality { get; set; } = "High";

    /// <summary>
    /// Target frame rate (fps)
    /// </summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>
    /// Codec: h264, h265, vp9, etc.
    /// </summary>
    public string VideoCodec { get; set; } = "h264";

    /// <summary>
    /// Audio codec: aac, libmp3lame, opus, etc.
    /// </summary>
    public string AudioCodec { get; set; } = "aac";
}
