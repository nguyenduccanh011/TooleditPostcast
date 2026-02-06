namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// A saved template for quick project creation
/// </summary>
public class Template
{
    /// <summary>
    /// Unique template ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Template name (displayed to user)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Template description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Full layout JSON (serialized project state without audio/assets)
    /// </summary>
    public string LayoutJson { get; set; } = "{}";

    /// <summary>
    /// Thumbnail image base64
    /// </summary>
    public string? ThumbnailBase64 { get; set; }

    /// <summary>
    /// When created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When last used
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Is this a system template (built-in)?
    /// </summary>
    public bool IsSystem { get; set; } = false;
}
