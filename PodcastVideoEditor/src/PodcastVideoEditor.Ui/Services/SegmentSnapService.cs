using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Pure-logic service for segment collision detection, magnetic snapping, and grid alignment.
/// Extracted from TimelineViewModel to enable unit testing and reduce god-class size.
/// </summary>
internal sealed class SegmentSnapService
{
    /// <summary>Snap time value to grid (2 decimal places to match script format).</summary>
    public double SnapToGrid(double timeSeconds, double gridSize)
    {
        return Math.Round(Math.Round(timeSeconds / gridSize) * gridSize, 2);
    }

    /// <summary>Check if two time ranges overlap.</summary>
    public bool HasOverlap(double start1, double end1, double start2, double end2)
    {
        return !(end1 <= start2 || end2 <= start1);
    }

    /// <summary>
    /// Check collision for a segment position within a track's segments.
    /// </summary>
    public bool CheckCollision(
        double testStart, double testEnd, string testId,
        IEnumerable<Segment> trackSegments, string? excludeId = null)
    {
        foreach (var other in trackSegments)
        {
            if (excludeId != null && other.Id == excludeId)
                continue;
            if (HasOverlap(testStart, testEnd, other.StartTime, other.EndTime))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Magnetic snap: if <paramref name="proposedTime"/> is within <paramref name="thresholdSeconds"/>
    /// of any segment edge in the track (excluding <paramref name="excludeSegmentId"/>),
    /// return the snapped edge time; otherwise return <paramref name="proposedTime"/>.
    /// </summary>
    public double SnapToSegmentEdge(
        double proposedTime,
        IEnumerable<Segment> trackSegments,
        string? excludeSegmentId,
        double thresholdSeconds)
    {
        double best = proposedTime;
        double bestDist = thresholdSeconds;

        foreach (var seg in trackSegments)
        {
            if (seg.Id == excludeSegmentId) continue;

            double dStart = Math.Abs(proposedTime - seg.StartTime);
            double dEnd = Math.Abs(proposedTime - seg.EndTime);
            if (dStart < bestDist) { bestDist = dStart; best = seg.StartTime; }
            if (dEnd < bestDist) { bestDist = dEnd; best = seg.EndTime; }
        }
        return best;
    }

    /// <summary>
    /// Cross-track magnetic snap: snap to nearest segment edge across ALL tracks except
    /// the segment's own track (where collision resolution already handles alignment).
    /// Returns the snapped time or the original proposedTime if no edge is nearby.
    /// </summary>
    public double SnapToCrossTrackEdge(
        double proposedTime,
        IEnumerable<Track> allTracks,
        string? excludeTrackId,
        string? excludeSegmentId,
        double thresholdSeconds)
    {
        double best = proposedTime;
        double bestDist = thresholdSeconds;

        foreach (var track in allTracks)
        {
            if (track.Id == excludeTrackId) continue;
            foreach (var seg in track.Segments)
            {
                if (seg.Id == excludeSegmentId) continue;

                double dStart = Math.Abs(proposedTime - seg.StartTime);
                double dEnd = Math.Abs(proposedTime - seg.EndTime);
                if (dStart < bestDist) { bestDist = dStart; best = seg.StartTime; }
                if (dEnd < bestDist) { bestDist = dEnd; best = seg.EndTime; }
            }
        }
        return best;
    }

    /// <summary>
    /// All-track magnetic snap: snap to nearest segment edge across ALL tracks.
    /// Used for move operations where we want to align with any track's segment edges.
    /// Returns the snapped time or the original proposedTime if no edge is nearby.
    /// </summary>
    public double SnapToAllTrackEdges(
        double proposedTime,
        IEnumerable<Track> allTracks,
        string? excludeSegmentId,
        double thresholdSeconds)
    {
        double best = proposedTime;
        double bestDist = thresholdSeconds;

        foreach (var track in allTracks)
        {
            foreach (var seg in track.Segments)
            {
                if (seg.Id == excludeSegmentId) continue;

                double dStart = Math.Abs(proposedTime - seg.StartTime);
                double dEnd = Math.Abs(proposedTime - seg.EndTime);
                if (dStart < bestDist) { bestDist = dStart; best = seg.StartTime; }
                if (dEnd < bestDist) { bestDist = dEnd; best = seg.EndTime; }
            }
        }
        return best;
    }

    /// <summary>
    /// When a move causes collision, try to snap the segment to the nearest valid boundary
    /// (before or after each segment in the track). Returns null if no valid position found.
    /// </summary>
    public (double start, double end)? TrySnapToBoundary(
        Segment segment,
        double requestedStart,
        double requestedEnd,
        IEnumerable<Segment> trackSegments,
        double totalDuration,
        double gridSize)
    {
        double duration = requestedEnd - requestedStart;
        double currentStart = segment.StartTime;
        (double start, double end)? best = null;
        double bestDistance = double.MaxValue;

        foreach (var other in trackSegments)
        {
            if (other.Id == segment.Id) continue;

            // Option A: Place after other segment
            double startA = SnapToGrid(other.EndTime, gridSize);
            double endA = startA + duration;
            if (endA > totalDuration && totalDuration > 0)
            {
                endA = totalDuration;
                startA = SnapToGrid(endA - duration, gridSize);
            }
            if (startA >= 0 && endA <= totalDuration + 0.001)
            {
                if (!CheckCollision(startA, endA, segment.Id, trackSegments, segment.Id))
                {
                    double dist = Math.Abs(startA - currentStart);
                    if (dist < bestDistance) { bestDistance = dist; best = (startA, endA); }
                }
            }

            // Option B: Place before other segment
            double endB = SnapToGrid(other.StartTime, gridSize);
            double startB = endB - duration;
            if (startB < 0)
            {
                startB = 0;
                endB = SnapToGrid(duration, gridSize);
            }
            if (startB >= 0 && endB <= totalDuration + 0.001)
            {
                if (!CheckCollision(startB, endB, segment.Id, trackSegments, segment.Id))
                {
                    double dist = Math.Abs(startB - currentStart);
                    if (dist < bestDistance) { bestDistance = dist; best = (startB, endB); }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Unified segment timing update with collision resolution and grid snapping.
    /// Returns the final (start, end) or null if the operation is blocked.
    /// </summary>
    public (double start, double end)? ResolveTiming(
        Segment segment,
        double newStartTime,
        double newEndTime,
        IEnumerable<Segment> trackSegments,
        double totalDuration,
        double gridSize,
        Action<double> expandTimeline)
    {
        double duration = newEndTime - newStartTime;
        if (duration <= 0) return null;

        const double timeTolerance = 0.001;
        bool startUnchanged = Math.Abs(newStartTime - segment.StartTime) < timeTolerance;
        bool endUnchanged = Math.Abs(newEndTime - segment.EndTime) < timeTolerance;
        bool resizeRightOnly = startUnchanged && !endUnchanged;
        bool resizeLeftOnly = endUnchanged && !startUnchanged;

        if (resizeRightOnly)
        {
            newStartTime = segment.StartTime;
            if (newEndTime > totalDuration)
                expandTimeline(newEndTime);
            newEndTime = SnapToGrid(newEndTime, gridSize);
        }
        else if (resizeLeftOnly)
        {
            newEndTime = segment.EndTime;
            if (newStartTime < 0) newStartTime = 0;
            newStartTime = SnapToGrid(newStartTime, gridSize);
        }
        else
        {
            if (newEndTime > totalDuration)
                expandTimeline(newEndTime);
            if (newStartTime < 0)
            {
                newStartTime = 0;
                newEndTime = duration;
            }
            newStartTime = SnapToGrid(newStartTime, gridSize);
            newEndTime = SnapToGrid(newEndTime, gridSize);
        }

        // Collision check
        if (CheckCollision(newStartTime, newEndTime, segment.Id, trackSegments, segment.Id))
        {
            if (resizeRightOnly)
            {
                double capAt = newEndTime;
                foreach (var other in trackSegments)
                {
                    if (other.Id == segment.Id) continue;
                    if (other.StartTime > segment.StartTime)
                        capAt = Math.Min(capAt, other.StartTime);
                }
                newStartTime = segment.StartTime;
                newEndTime = Math.Min(SnapToGrid(newEndTime, gridSize), capAt);
                if (newEndTime <= newStartTime) return null;
            }
            else if (resizeLeftOnly)
            {
                double floorAt = newStartTime;
                foreach (var other in trackSegments)
                {
                    if (other.Id == segment.Id) continue;
                    if (other.EndTime < segment.EndTime)
                        floorAt = Math.Max(floorAt, other.EndTime);
                }
                newEndTime = segment.EndTime;
                newStartTime = Math.Max(SnapToGrid(newStartTime, gridSize), floorAt);
                if (newEndTime <= newStartTime) return null;
            }
            else
            {
                var snapped = TrySnapToBoundary(segment, newStartTime, newEndTime, trackSegments, totalDuration, gridSize);
                if (snapped.HasValue)
                {
                    newStartTime = snapped.Value.start;
                    newEndTime = snapped.Value.end;
                }
                else
                {
                    return null;
                }
            }
        }

        return (newStartTime, newEndTime);
    }
}
