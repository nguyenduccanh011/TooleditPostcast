namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// A visual element on the canvas (text, image, logo, visualizer, etc.)
/// </summary>
public class Element
{
    /// <summary>
    /// Unique element ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Project this element belongs to
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Element type: Title, Logo, ScriptText, Visualizer, Image, Video, Shape
    /// </summary>
    public string Type { get; set; } = "Title";

    /// <summary>
    /// X position (0-1 normalized, or pixel)
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Y position (0-1 normalized, or pixel)
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Width of element
    /// </summary>
    public double Width { get; set; } = 100;

    /// <summary>
    /// Height of element
    /// </summary>
    public double Height { get; set; } = 50;

    /// <summary>
    /// Rotation angle in degrees
    /// </summary>
    public double Rotation { get; set; } = 0;

    /// <summary>
    /// Z-index (layer order)
    /// </summary>
    public int ZIndex { get; set; }

    /// <summary>
    /// Opacity (0-1)
    /// </summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    /// Additional properties as JSON (font, color, text, etc.)
    /// </summary>
    public string PropertiesJson { get; set; } = "{}";

    /// <summary>
    /// Is element visible?
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// When created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional: segment this element is attached to. When set, element is only visible during segment's [StartTime, EndTime].
    /// Null = global overlay (always visible or until explicitly attached).
    /// </summary>
    public string? SegmentId { get; set; }

    // Navigation
    public Project? Project { get; set; }

    /// <summary>
    /// Segment this element belongs to (when SegmentId is set).
    /// </summary>
    public Segment? Segment { get; set; }
}
