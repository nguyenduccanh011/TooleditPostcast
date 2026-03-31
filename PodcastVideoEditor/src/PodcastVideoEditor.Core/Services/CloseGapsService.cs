#nullable enable
using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Closes timing gaps between consecutive segments on a track by extending
/// each segment's EndTime to meet the next segment's StartTime.
/// </summary>
public static class CloseGapsService
{
    /// <summary>
    /// Extends the EndTime of each segment to meet the StartTime of the next segment,
    /// eliminating black-frame gaps between scenes.
    /// </summary>
    /// <param name="segments">Segments to process (typically from the same track).</param>
    /// <param name="maxGapSeconds">
    /// If set, only close gaps smaller than or equal to this threshold (seconds).
    /// Gaps larger than this are considered intentional pauses and left untouched.
    /// If null (default), all gaps are closed regardless of size.
    /// </param>
    /// <returns>
    /// List of changes made: (segment, oldEndTime, newEndTime) for each modified segment.
    /// Empty if no gaps were found.
    /// </returns>
    public static List<GapChange> CloseGaps(IList<Segment> segments, double? maxGapSeconds = null)
    {
        var changes = new List<GapChange>();
        if (segments == null || segments.Count < 2)
            return changes;

        // Sort by StartTime to process in chronological order
        var sorted = segments.OrderBy(s => s.StartTime).ToList();

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var current = sorted[i];
            var next = sorted[i + 1];

            double gap = next.StartTime - current.EndTime;

            // Skip if no gap (already touching or overlapping)
            if (gap <= 0)
                continue;

            // Skip if gap exceeds threshold
            if (maxGapSeconds.HasValue && gap > maxGapSeconds.Value)
                continue;

            double oldEnd = current.EndTime;
            current.EndTime = next.StartTime;
            changes.Add(new GapChange(current, oldEnd, current.EndTime));
        }

        return changes;
    }

    /// <summary>
    /// Close gaps on paired tracks (text + visual) in lockstep.
    /// Both track segment lists must have the same count and matching StartTimes from the same import.
    /// Falls back to independent closing if pairing doesn't match.
    /// </summary>
    public static (List<GapChange> trackAChanges, List<GapChange> trackBChanges) CloseGapsPaired(
        IList<Segment> trackASegments,
        IList<Segment> trackBSegments,
        double? maxGapSeconds = null)
    {
        // If segment counts match and they share timing, close in lockstep
        var sortedA = trackASegments.OrderBy(s => s.StartTime).ToList();
        var sortedB = trackBSegments.OrderBy(s => s.StartTime).ToList();

        if (sortedA.Count == sortedB.Count && sortedA.Count >= 2 && TimingsMatch(sortedA, sortedB))
        {
            var changesA = new List<GapChange>();
            var changesB = new List<GapChange>();

            for (int i = 0; i < sortedA.Count - 1; i++)
            {
                var currentA = sortedA[i];
                var nextA = sortedA[i + 1];
                double gap = nextA.StartTime - currentA.EndTime;

                if (gap <= 0)
                    continue;
                if (maxGapSeconds.HasValue && gap > maxGapSeconds.Value)
                    continue;

                double oldEndA = currentA.EndTime;
                currentA.EndTime = nextA.StartTime;
                changesA.Add(new GapChange(currentA, oldEndA, currentA.EndTime));

                var currentB = sortedB[i];
                double oldEndB = currentB.EndTime;
                currentB.EndTime = sortedB[i + 1].StartTime;
                changesB.Add(new GapChange(currentB, oldEndB, currentB.EndTime));
            }

            return (changesA, changesB);
        }

        // Fallback: close independently
        return (CloseGaps(trackASegments, maxGapSeconds), CloseGaps(trackBSegments, maxGapSeconds));
    }

    private static bool TimingsMatch(List<Segment> a, List<Segment> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].StartTime - b[i].StartTime) > 0.01 ||
                Math.Abs(a[i].EndTime - b[i].EndTime) > 0.01)
                return false;
        }
        return true;
    }
}

/// <summary>Records a single gap-closure change for undo support.</summary>
public record GapChange(Segment Segment, double OldEndTime, double NewEndTime);
