using PodcastVideoEditor.Core.Models;
using Serilog;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Handles propagation of text style changes across all TextOverlayElements on the same track.
/// Implements the CapCut-style pattern where all text segments on a track share visual style,
/// and changing one segment's style/position updates all siblings.
/// </summary>
public class TrackStylePropagationService
{
    /// <summary>
    /// Properties that are per-segment and should NOT be propagated.
    /// </summary>
    private static readonly HashSet<string> ExcludedProperties = new(StringComparer.Ordinal)
    {
        "Content",    // Text content is unique per segment
        "SegmentId",  // Binding to segment is per-element
        "Id",         // Element identity
        "Name",       // Display name
        "ZIndex",     // Computed from track order
        "IsVisible",  // Managed by visibility system
        "IsSelected", // UI state
        "CreatedAt",  // Metadata
    };

    /// <summary>
    /// Propagate all shared style properties from a source element to all siblings on the same track.
    /// Updates Track.TextStyleJson as the source of truth.
    /// </summary>
    /// <param name="source">The element whose style was just modified.</param>
    /// <param name="track">The text track containing the source element.</param>
    /// <param name="siblings">All TextOverlayElements on the same track (including source).</param>
    public void PropagateStyleFromElement(TextOverlayElement source, Track track, IEnumerable<TextOverlayElement> siblings)
    {
        var template = TextStyleTemplate.CaptureFrom(source);
        track.TextStyle = template;

        foreach (var sibling in siblings)
        {
            if (ReferenceEquals(sibling, source))
                continue;

            var content = sibling.Content;
            var segmentId = sibling.SegmentId;
            var id = sibling.Id;
            var name = sibling.Name;
            var zIndex = sibling.ZIndex;
            var isVisible = sibling.IsVisible;

            template.ApplyTo(sibling);

            // Restore per-element properties
            sibling.Content = content;
            sibling.SegmentId = segmentId;
            sibling.Id = id;
            sibling.Name = name;
            sibling.ZIndex = zIndex;
            sibling.IsVisible = isVisible;
        }

        Log.Debug("Propagated text style from element {ElementId} to {Count} siblings on track {TrackId}",
            source.Id, siblings.Count() - 1, track.Id);
    }

    /// <summary>
    /// Propagate only position (X, Y, Width, Height) from source to siblings.
    /// Used during canvas drag/resize for performance — avoids updating all style properties on every mouse move.
    /// </summary>
    public void PropagatePositionFromElement(TextOverlayElement source, IEnumerable<TextOverlayElement> siblings)
    {
        foreach (var sibling in siblings)
        {
            if (ReferenceEquals(sibling, source))
                continue;
            sibling.X = source.X;
            sibling.Y = source.Y;
            sibling.Width = source.Width;
            sibling.Height = source.Height;
        }
    }

    /// <summary>
    /// Propagate a single named property from source to all siblings.
    /// Used by PropertyEditorViewModel when a single property is edited.
    /// Skips propagation for per-segment properties (Content, SegmentId, etc.).
    /// </summary>
    /// <returns>true if propagation occurred, false if the property is excluded.</returns>
    public bool PropagateSingleProperty(TextOverlayElement source, string propertyName,
        Track track, IEnumerable<TextOverlayElement> siblings)
    {
        if (ExcludedProperties.Contains(propertyName))
            return false;

        var prop = typeof(TextOverlayElement).GetProperty(propertyName)
                   ?? typeof(CanvasElement).GetProperty(propertyName);

        if (prop == null)
            return false;

        var value = prop.GetValue(source);

        foreach (var sibling in siblings)
        {
            if (ReferenceEquals(sibling, source))
                continue;
            prop.SetValue(sibling, value);
        }

        // Update track template
        track.TextStyle = TextStyleTemplate.CaptureFrom(source);

        Log.Debug("Propagated property '{Property}' from element {ElementId} to siblings on track {TrackId}",
            propertyName, source.Id, track.Id);

        return true;
    }

    /// <summary>
    /// Initialize a track's TextStyleTemplate from the first element.
    /// Called lazily when a track has no template yet and an element is first edited.
    /// </summary>
    public TextStyleTemplate InitializeTrackTemplate(Track track, TextOverlayElement element)
    {
        var template = TextStyleTemplate.CaptureFrom(element);
        track.TextStyle = template;
        Log.Information("Initialized text style template for track {TrackId} from element {ElementId}",
            track.Id, element.Id);
        return template;
    }

    /// <summary>
    /// Check whether a property name is excluded from propagation (per-segment property).
    /// </summary>
    public static bool IsExcludedProperty(string propertyName)
        => ExcludedProperties.Contains(propertyName);
}
