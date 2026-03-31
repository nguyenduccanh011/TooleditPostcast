using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class CloseGapsServiceTests
{
    private static Segment MakeSegment(double start, double end, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        StartTime = start,
        EndTime = end,
        Kind = SegmentKind.Text
    };

    [Fact]
    public void CloseGaps_ExtendsEndTimeToNextStart()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),
            MakeSegment(2.74, 5.58),
            MakeSegment(5.60, 9.00),
        };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Equal(2, changes.Count);
        Assert.Equal(2.74, segments[0].EndTime); // was 2.42, extended to 2.74
        Assert.Equal(5.60, segments[1].EndTime); // was 5.58, extended to 5.60
        Assert.Equal(9.00, segments[2].EndTime); // last segment unchanged
    }

    [Fact]
    public void CloseGaps_NoGaps_NoChanges()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 2.74),
            MakeSegment(2.74, 5.60),
            MakeSegment(5.60, 9.00),
        };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Empty(changes);
    }

    [Fact]
    public void CloseGaps_Overlapping_NoChanges()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 3.00),
            MakeSegment(2.50, 5.00), // overlaps with first
        };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Empty(changes);
        Assert.Equal(3.00, segments[0].EndTime); // unchanged
    }

    [Fact]
    public void CloseGaps_RespectsMaxGapThreshold()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),  // gap = 0.32s (should close)
            MakeSegment(2.74, 5.58),  // gap = 3.42s (should NOT close, > 1.0)
            MakeSegment(9.00, 12.00),
        };

        var changes = CloseGapsService.CloseGaps(segments, maxGapSeconds: 1.0);

        Assert.Single(changes);
        Assert.Equal(2.74, segments[0].EndTime); // closed
        Assert.Equal(5.58, segments[1].EndTime); // NOT closed (gap 3.42 > 1.0)
    }

    [Fact]
    public void CloseGaps_SingleSegment_NoChanges()
    {
        var segments = new List<Segment> { MakeSegment(0.00, 5.00) };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Empty(changes);
    }

    [Fact]
    public void CloseGaps_EmptyList_NoChanges()
    {
        var changes = CloseGapsService.CloseGaps(new List<Segment>());
        Assert.Empty(changes);
    }

    [Fact]
    public void CloseGaps_UnsortedInput_ProcessesChronologically()
    {
        var seg1 = MakeSegment(5.60, 9.00);
        var seg2 = MakeSegment(0.00, 2.42);
        var seg3 = MakeSegment(2.74, 5.58);
        var segments = new List<Segment> { seg1, seg2, seg3 };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Equal(2, changes.Count);
        Assert.Equal(2.74, seg2.EndTime); // was 2.42 → extended to 2.74
        Assert.Equal(5.60, seg3.EndTime); // was 5.58 → extended to 5.60
    }

    [Fact]
    public void CloseGaps_ReturnsCorrectOldEndTimes()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),
            MakeSegment(2.74, 5.58),
        };

        var changes = CloseGapsService.CloseGaps(segments);

        Assert.Single(changes);
        Assert.Equal(2.42, changes[0].OldEndTime);
        Assert.Equal(2.74, changes[0].NewEndTime);
    }

    [Fact]
    public void CloseGapsPaired_SynchronizesBothTracks()
    {
        var textSegments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),
            MakeSegment(2.74, 5.58),
            MakeSegment(5.60, 9.00),
        };
        var visualSegments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),
            MakeSegment(2.74, 5.58),
            MakeSegment(5.60, 9.00),
        };

        var (changesA, changesB) = CloseGapsService.CloseGapsPaired(textSegments, visualSegments);

        Assert.Equal(2, changesA.Count);
        Assert.Equal(2, changesB.Count);

        // Both tracks should have identical timing after closure
        Assert.Equal(textSegments[0].EndTime, visualSegments[0].EndTime);
        Assert.Equal(textSegments[1].EndTime, visualSegments[1].EndTime);
    }

    [Fact]
    public void CloseGapsPaired_MismatchedCounts_ClosesIndependently()
    {
        var textSegments = new List<Segment>
        {
            MakeSegment(0.00, 2.42),
            MakeSegment(2.74, 5.58),
        };
        var visualSegments = new List<Segment>
        {
            MakeSegment(0.00, 3.00),
            MakeSegment(3.50, 6.00),
            MakeSegment(6.20, 9.00),
        };

        var (changesA, changesB) = CloseGapsService.CloseGapsPaired(textSegments, visualSegments);

        // Each track closed independently due to mismatched count
        Assert.Single(changesA);
        Assert.Equal(2, changesB.Count);
    }

    [Fact]
    public void CloseGaps_LastSegmentNeverExtended()
    {
        var segments = new List<Segment>
        {
            MakeSegment(0.00, 2.00),
            MakeSegment(3.00, 5.00),
        };

        CloseGapsService.CloseGaps(segments);

        // Last segment EndTime should stay at 5.00
        var last = segments.OrderBy(s => s.StartTime).Last();
        Assert.Equal(5.00, last.EndTime);
    }
}
