#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Manages multi-segment selection state for the timeline.
/// Uses HashSet for O(1) lookup, supports range-select anchor,
/// rubber-band selection, and fires SelectionChanged events.
/// </summary>
public sealed class TimelineSelectionService
{
    private readonly HashSet<Segment> _selected = new();
    private Segment? _rangeAnchor;

    /// <summary>Fired after every mutation so the UI can refresh.</summary>
    public event Action? SelectionChanged;

    /// <summary>Current multi-selected segments (read-only snapshot).</summary>
    public IReadOnlyCollection<Segment> SelectedSegments => _selected;

    /// <summary>Number of currently selected segments.</summary>
    public int Count => _selected.Count;

    /// <summary>True when one or more segments are selected.</summary>
    public bool HasSelection => _selected.Count > 0;

    /// <summary>Check if a specific segment is in the selection.</summary>
    public bool IsSelected(Segment segment) => _selected.Contains(segment);

    // ── Single operations ────────────────────────────────────────────────

    /// <summary>
    /// Toggle a segment in/out of multi-selection (Ctrl+Click).
    /// Updates the range anchor to the toggled segment.
    /// </summary>
    public void Toggle(Segment segment)
    {
        if (_selected.Contains(segment))
        {
            _selected.Remove(segment);
            segment.IsMultiSelected = false;
        }
        else
        {
            _selected.Add(segment);
            segment.IsMultiSelected = true;
            _rangeAnchor = segment;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Range-select from the anchor to the target segment (Shift+Click).
    /// Selects all segments between anchor and target on the same track (sorted by time).
    /// If anchor is on a different track, selects all segments between them across tracks.
    /// </summary>
    public void RangeSelect(Segment target, IEnumerable<Track> tracks)
    {
        if (_rangeAnchor == null)
        {
            // No anchor: treat as a toggle
            Toggle(target);
            return;
        }

        // Build a flat ordered list of all segments across tracks
        var allSegments = tracks
            .OrderBy(t => t.Order)
            .SelectMany(t => t.Segments.OrderBy(s => s.StartTime))
            .ToList();

        int anchorIdx = allSegments.IndexOf(_rangeAnchor);
        int targetIdx = allSegments.IndexOf(target);

        if (anchorIdx < 0 || targetIdx < 0)
        {
            // Anchor or target not found — fall back to toggle
            Toggle(target);
            return;
        }

        int lo = Math.Min(anchorIdx, targetIdx);
        int hi = Math.Max(anchorIdx, targetIdx);

        // Clear current selection then select the range
        ClearInternal();
        for (int i = lo; i <= hi; i++)
        {
            var seg = allSegments[i];
            _selected.Add(seg);
            seg.IsMultiSelected = true;
        }

        // Keep anchor stable (don't move it on shift+click)
        SelectionChanged?.Invoke();
    }

    // ── Bulk operations ──────────────────────────────────────────────────

    /// <summary>Clear all multi-selected segments without deleting them.</summary>
    public void Clear()
    {
        if (_selected.Count == 0) return;
        ClearInternal();
        _rangeAnchor = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>Select all segments across all tracks (Ctrl+A).</summary>
    public void SelectAll(IEnumerable<Track> tracks)
    {
        ClearInternal();
        foreach (var track in tracks)
        {
            foreach (var seg in track.Segments)
            {
                _selected.Add(seg);
                seg.IsMultiSelected = true;
            }
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Select all segments on a specific track (Ctrl+Click track header).</summary>
    public void SelectTrack(Track track)
    {
        ClearInternal();
        foreach (var seg in track.Segments)
        {
            _selected.Add(seg);
            seg.IsMultiSelected = true;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Rubber-band (marquee) selection: select all segments whose time range
    /// overlaps the given rectangle (time range + track set).
    /// When <paramref name="additive"/> is true (Ctrl held), adds to existing selection.
    /// </summary>
    public void RubberBandSelect(
        double startTime, double endTime,
        IReadOnlyList<Track> hitTracks,
        bool additive)
    {
        if (!additive)
            ClearInternal();

        foreach (var track in hitTracks)
        {
            foreach (var seg in track.Segments)
            {
                // Overlap test: seg overlaps [startTime, endTime]
                if (seg.StartTime < endTime && seg.EndTime > startTime)
                {
                    if (_selected.Add(seg))
                        seg.IsMultiSelected = true;
                }
            }
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Set the range anchor explicitly (e.g. on normal click select).
    /// </summary>
    public void SetAnchor(Segment? segment)
    {
        _rangeAnchor = segment;
    }

    /// <summary>
    /// Remove a specific segment from the selection (e.g. when it's deleted).
    /// </summary>
    public void Remove(Segment segment)
    {
        if (_selected.Remove(segment))
        {
            segment.IsMultiSelected = false;
            if (_rangeAnchor == segment)
                _rangeAnchor = null;
            SelectionChanged?.Invoke();
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private void ClearInternal()
    {
        foreach (var seg in _selected)
            seg.IsMultiSelected = false;
        _selected.Clear();
    }
}
