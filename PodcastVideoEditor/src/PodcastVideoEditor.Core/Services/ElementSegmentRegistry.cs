#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Provides O(1) lookups between canvas elements and timeline segments.
/// Replaces ad-hoc FirstOrDefault(e => e.SegmentId == ...) scattered across
/// the codebase, which are O(n) per lookup.
///
/// Build once from a snapshot of elements and segments, then query freely.
/// </summary>
public sealed class ElementSegmentRegistry
{
    private readonly Dictionary<string, List<CanvasElement>> _segmentToElements;
    private readonly Dictionary<string, Segment> _segmentById;

    private ElementSegmentRegistry(
        Dictionary<string, List<CanvasElement>> segmentToElements,
        Dictionary<string, Segment> segmentById)
    {
        _segmentToElements = segmentToElements;
        _segmentById = segmentById;
    }

    /// <summary>
    /// Build a registry from a set of canvas elements and project tracks.
    /// </summary>
    public static ElementSegmentRegistry Build(
        IEnumerable<CanvasElement>? elements,
        IEnumerable<Track>? tracks)
    {
        var segToElements = new Dictionary<string, List<CanvasElement>>(StringComparer.Ordinal);
        var segById = new Dictionary<string, Segment>(StringComparer.Ordinal);

        if (tracks != null)
        {
            foreach (var track in tracks)
            {
                if (track.Segments == null) continue;
                foreach (var seg in track.Segments)
                {
                    if (!string.IsNullOrEmpty(seg.Id))
                        segById.TryAdd(seg.Id, seg);
                }
            }
        }

        if (elements != null)
        {
            foreach (var el in elements)
            {
                if (string.IsNullOrEmpty(el.SegmentId)) continue;

                if (!segToElements.TryGetValue(el.SegmentId, out var list))
                {
                    list = new List<CanvasElement>(1);
                    segToElements[el.SegmentId] = list;
                }
                list.Add(el);
            }
        }

        return new ElementSegmentRegistry(segToElements, segById);
    }

    /// <summary>
    /// Get the first canvas element linked to the given segment, or null.
    /// </summary>
    public CanvasElement? GetElementForSegment(string? segmentId)
    {
        if (string.IsNullOrEmpty(segmentId)) return null;
        return _segmentToElements.TryGetValue(segmentId, out var list) && list.Count > 0
            ? list[0]
            : null;
    }

    /// <summary>
    /// Get all canvas elements linked to the given segment.
    /// </summary>
    public IReadOnlyList<CanvasElement> GetElementsForSegment(string? segmentId)
    {
        if (string.IsNullOrEmpty(segmentId)) return [];
        return _segmentToElements.TryGetValue(segmentId, out var list)
            ? list
            : [];
    }

    /// <summary>
    /// Get the timeline segment for a given segment ID.
    /// </summary>
    public Segment? GetSegmentById(string? segmentId)
    {
        if (string.IsNullOrEmpty(segmentId)) return null;
        return _segmentById.TryGetValue(segmentId, out var seg) ? seg : null;
    }
}
