namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// An imported asset (image, video, audio, etc.)
/// </summary>
public class Asset
{
    /// <summary>
    /// Unique asset ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Project this asset belongs to
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Asset type: Image, Video, Audio, Icon
    /// </summary>
    public string Type { get; set; } = "Image";

    /// <summary>
    /// Full path to asset file (stored in AppData)
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Original file name
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// File extension (jpg, png, mp4, mp3, etc.)
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Width (for images/videos)
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Height (for images/videos)
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Duration in seconds (for videos/audio)
    /// </summary>
    public double? Duration { get; set; }

    /// <summary>
    /// When imported
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Project? Project { get; set; }
}
