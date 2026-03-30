using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Pure-logic service for timeline layout calculation: pixels-per-second,
/// duration computation, and timeline-to-pixel conversions.
/// Extracted from TimelineViewModel to enable unit testing and reduce god-class size.
/// </summary>
internal sealed class TimelineLayoutService
{
    /// <summary>
    /// Calculate pixels per second based on timeline width and total duration.
    /// Returns the clamped PPS value.
    /// </summary>
    public double CalculatePixelsPerSecond(double timelineWidth, double totalDuration)
    {
        if (totalDuration <= 0)
            totalDuration = TimelineConstants.DefaultEmptyDuration;

        double displayDuration = Math.Max(totalDuration + TimelineConstants.DisplayDurationBuffer, TimelineConstants.DefaultEmptyDuration);
        double pps = timelineWidth / displayDuration;

        return Math.Max(pps, TimelineConstants.MinPixelsPerSecond);
    }

    /// <summary>
    /// Returns the latest EndTime across all segments on all tracks, or 0 if none.
    /// </summary>
    public double ComputeMaxSegmentEndTime(IEnumerable<Track> tracks)
    {
        double max = 0;
        foreach (var track in tracks)
            foreach (var seg in track.Segments)
                if (seg.EndTime > max)
                    max = seg.EndTime;
        return max;
    }

    /// <summary>
    /// Compute TotalDuration from segment data and audio player duration.
    /// Always keeps timeline at least as long as any loaded audio.
    /// Does NOT change TimelineWidth – the user's zoom level is preserved.
    /// Returns (totalDuration, pixelsPerSecond).
    /// </summary>
    public (double totalDuration, double pixelsPerSecond) RecalculateDuration(
        IEnumerable<Track> tracks,
        double audioDuration,
        double currentTimelineWidth)
    {
        var segEnd = ComputeMaxSegmentEndTime(tracks);
        double computed = segEnd > 0 ? segEnd + TimelineConstants.SegmentEndBuffer : TimelineConstants.DefaultEmptyDuration;
        double totalDuration = Math.Max(computed, audioDuration);
        double pps = CalculatePixelsPerSecond(currentTimelineWidth, totalDuration);
        return (totalDuration, pps);
    }

    /// <summary>
    /// Compute a suitable TimelineWidth to fit all content (for initial project load / zoom-to-fit).
    /// </summary>
    public double ComputeFitWidth(double totalDuration)
    {
        return Math.Max(TimelineConstants.MinTimelineWidth, totalDuration * 10);
    }

    /// <summary>Convert time (seconds) to pixel position on timeline.</summary>
    public double TimeToPixels(double timeSeconds, double pixelsPerSecond)
        => timeSeconds * pixelsPerSecond;

    /// <summary>Convert pixel position to time (seconds) on timeline.</summary>
    public double PixelsToTime(double pixelX, double pixelsPerSecond)
        => pixelsPerSecond > 0 ? pixelX / pixelsPerSecond : 0;
}
