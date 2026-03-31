#nullable enable
namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// A global library asset (shared across all projects).
/// Can be built-in (shipped with app) or user-imported.
/// </summary>
public class GlobalAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Category: Logo, Icon, Overlay, Border, Uncategorized
    /// </summary>
    public string Category { get; set; } = "Uncategorized";

    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>
    /// True = shipped with the application (read-only, cannot be deleted by user).
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Comma-separated searchable tags.
    /// </summary>
    public string? Tags { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
