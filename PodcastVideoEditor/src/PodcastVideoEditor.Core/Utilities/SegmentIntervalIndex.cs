using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Binary-search index for non-overlapping segments within a single track.
/// O(n log n) build, O(log n) point query — replaces O(n) linear scan.
/// </summary>
public sealed class SegmentIntervalIndex
{
    private readonly Segment[] _sorted;

    public SegmentIntervalIndex(IEnumerable<Segment> segments)
    {
        _sorted = segments.OrderBy(s => s.StartTime).ToArray();
    }

    /// <summary>
    /// Find the segment containing <paramref name="time"/> (StartTime &lt;= time &lt; EndTime).
    /// Returns null if no segment contains the given time.
    /// </summary>
    public Segment? FindAt(double time)
    {
        int lo = 0, hi = _sorted.Length - 1;
        int candidate = -1;

        // Binary search: find last segment whose StartTime <= time
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_sorted[mid].StartTime <= time)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (candidate >= 0 && time < _sorted[candidate].EndTime)
            return _sorted[candidate];

        return null;
    }

    /// <summary>
    /// Check whether any segment (other than <paramref name="excludeId"/>) overlaps with
    /// the range [<paramref name="start"/>, <paramref name="end"/>).
    /// Uses binary search to skip segments that end before <paramref name="start"/>.
    /// O(log n + k) where k is the number of candidates checked.
    /// </summary>
    public bool HasOverlap(double start, double end, string? excludeId = null)
    {
        if (_sorted.Length == 0) return false;

        // Binary search: find first segment whose StartTime < end
        // Any segment with StartTime >= end cannot overlap.
        int lo = 0, hi = _sorted.Length - 1;
        int firstCandidate = _sorted.Length; // default: no candidates

        // Find the leftmost segment whose StartTime >= start (or whose EndTime > start)
        // Actually, we need all segments where: seg.StartTime < end AND seg.EndTime > start
        // Since sorted by StartTime, find first index where StartTime < end, scan from there backward/forward.

        // Find the first segment index where StartTime >= end (everything before this is a candidate)
        lo = 0; hi = _sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_sorted[mid].StartTime < end)
                lo = mid + 1;
            else
                hi = mid;
        }
        int upperBound = lo; // _sorted[upperBound..] all have StartTime >= end (no overlap possible)

        // Check candidates [0..upperBound) — any whose EndTime > start overlaps
        for (int i = upperBound - 1; i >= 0; i--)
        {
            var seg = _sorted[i];
            if (seg.EndTime <= start)
                break; // Since sorted by StartTime and non-overlapping, earlier segments also won't overlap
            if (excludeId != null && seg.Id == excludeId)
                continue;
            return true; // overlap found
        }
        return false;
    }

    public int Count => _sorted.Length;
}
