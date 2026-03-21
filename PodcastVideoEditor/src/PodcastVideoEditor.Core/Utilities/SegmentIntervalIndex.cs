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

    public int Count => _sorted.Length;
}
