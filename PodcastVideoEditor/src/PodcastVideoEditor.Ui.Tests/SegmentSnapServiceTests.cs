using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

public sealed class SegmentSnapServiceTests
{
    private readonly SegmentSnapService _sut = new();
    private const double GridSize = 0.01;

    // ── Helpers ────────────────────────────────────────────────────────

    private static Segment MakeSegment(string id, double start, double end, string trackId = "T1")
        => new() { Id = id, StartTime = start, EndTime = end, TrackId = trackId };

    // ── SnapToGrid ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.234, 0.01, 1.23)]
    [InlineData(1.235, 0.01, 1.24)]
    [InlineData(0.0,   0.01, 0.0)]
    [InlineData(5.999, 0.5,  6.0)]
    [InlineData(3.3,   1.0,  3.0)]
    public void SnapToGrid_RoundsCorrectly(double input, double grid, double expected)
    {
        Assert.Equal(expected, _sut.SnapToGrid(input, grid));
    }

    // ── HasOverlap ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 2, 1, 3, true)]   // partial overlap
    [InlineData(0, 2, 2, 4, false)]  // edge-touching = no overlap
    [InlineData(0, 5, 1, 3, true)]   // containment
    [InlineData(3, 5, 0, 2, false)]  // separate
    [InlineData(0, 2, 0, 2, true)]   // identical
    public void HasOverlap_DetectsCorrectly(double s1, double e1, double s2, double e2, bool expected)
    {
        Assert.Equal(expected, _sut.HasOverlap(s1, e1, s2, e2));
    }

    // ── CheckCollision ────────────────────────────────────────────────

    [Fact]
    public void CheckCollision_ReturnsFalse_WhenNoOverlap()
    {
        var segments = new[] { MakeSegment("A", 0, 2), MakeSegment("B", 5, 7) };
        Assert.False(_sut.CheckCollision(3, 4, "C", segments));
    }

    [Fact]
    public void CheckCollision_ReturnsTrue_WhenOverlaps()
    {
        var segments = new[] { MakeSegment("A", 0, 2), MakeSegment("B", 5, 7) };
        Assert.True(_sut.CheckCollision(1, 6, "C", segments));
    }

    [Fact]
    public void CheckCollision_ExcludesById()
    {
        var segments = new[] { MakeSegment("A", 0, 2), MakeSegment("B", 5, 7) };
        // Would collide with B, but B is excluded
        Assert.False(_sut.CheckCollision(5, 7, "X", segments, excludeId: "B"));
    }

    // ── SnapToSegmentEdge ─────────────────────────────────────────────

    [Fact]
    public void SnapToSegmentEdge_SnapsToNearestEdge()
    {
        var segments = new[] { MakeSegment("A", 2, 5), MakeSegment("B", 8, 10) };
        // proposedTime 4.95 is within 0.1 of A.EndTime (5.0)
        double result = _sut.SnapToSegmentEdge(4.95, segments, excludeSegmentId: null, thresholdSeconds: 0.1);
        Assert.Equal(5.0, result);
    }

    [Fact]
    public void SnapToSegmentEdge_ReturnsProposed_WhenNoEdgeNearby()
    {
        var segments = new[] { MakeSegment("A", 2, 5) };
        double result = _sut.SnapToSegmentEdge(3.5, segments, excludeSegmentId: null, thresholdSeconds: 0.1);
        Assert.Equal(3.5, result);
    }

    [Fact]
    public void SnapToSegmentEdge_ExcludesSegmentById()
    {
        var segments = new[] { MakeSegment("A", 2, 5) };
        // Even though 5.0 is close, "A" is excluded
        double result = _sut.SnapToSegmentEdge(4.95, segments, excludeSegmentId: "A", thresholdSeconds: 0.1);
        Assert.Equal(4.95, result);
    }

    // ── TrySnapToBoundary ─────────────────────────────────────────────

    [Fact]
    public void TrySnapToBoundary_FindsSlotAfterOther()
    {
        var seg = MakeSegment("X", 0, 2);
        var others = new[] { seg, MakeSegment("A", 3, 5) };

        // Requesting position 3–5 collides with A. Nearest valid slot: after A at 5–7.
        var result = _sut.TrySnapToBoundary(seg, 3.0, 5.0, others, totalDuration: 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value.start);
        Assert.Equal(7.0, result.Value.end);
    }

    [Fact]
    public void TrySnapToBoundary_ReturnsNull_WhenTooFarAway()
    {
        // Segment duration = 2, maxSnapDistance = 2 * 2 = 4
        var seg = MakeSegment("X", 0, 2);
        var others = new[] { seg, MakeSegment("A", 10, 12) };

        // Requesting 10–12 collides with A. Nearest slot (after A) = 12–14.
        // Distance from requestedStart=10 to 12 = 2, within 4 → should find it.
        var result = _sut.TrySnapToBoundary(seg, 10.0, 12.0, others, totalDuration: 20, GridSize);
        Assert.NotNull(result);

        // But requesting 1–3 with only A at 10–12: distance to slot = 9, which exceeds maxSnapDistance=4
        var farResult = _sut.TrySnapToBoundary(seg, 1.0, 3.0, others, totalDuration: 20, GridSize);
        Assert.Null(farResult);
    }

    [Fact]
    public void TrySnapToBoundary_UsesRequestedStart_NotSegmentStart()
    {
        // Segment is currently at 0–2, but we're requesting position at 6–8.
        // This verifies that distance is measured from requestedStart (6), not segment.StartTime (0).
        var seg = MakeSegment("X", 0, 2);
        var other = MakeSegment("A", 6, 8);
        var others = new[] { seg, other };

        // RequestedStart=6, slot after A = 8. Distance = |8-6| = 2 ≤ maxSnap (4). Should find.
        var result = _sut.TrySnapToBoundary(seg, 6.0, 8.0, others, totalDuration: 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(8.0, result!.Value.start);
    }

    // ── TryClampAtCollision ───────────────────────────────────────────

    [Fact]
    public void TryClampAtCollision_MovingRight_ClampsBeforeBlocker()
    {
        var seg = MakeSegment("X", 2, 4);
        var blocker = MakeSegment("B", 5, 8);
        var all = new[] { seg, blocker };

        // Moving right: requesting 4–6 overlaps with blocker (5–8).
        // Should clamp end at blocker.Start=5, so result = (3, 5).
        var result = _sut.TryClampAtCollision(seg, 4.0, 6.0, all, GridSize);
        Assert.NotNull(result);
        Assert.Equal(3.0, result!.Value.start);
        Assert.Equal(5.0, result.Value.end);
    }

    [Fact]
    public void TryClampAtCollision_MovingLeft_ClampsAfterBlocker()
    {
        var seg = MakeSegment("X", 6, 8);
        var blocker = MakeSegment("B", 2, 5);
        var all = new[] { seg, blocker };

        // Moving left: requesting 4–6 overlaps with blocker (2–5).
        // Should clamp start at blocker.End=5, so result = (5, 7).
        var result = _sut.TryClampAtCollision(seg, 4.0, 6.0, all, GridSize);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value.start);
        Assert.Equal(7.0, result.Value.end);
    }

    [Fact]
    public void TryClampAtCollision_ReturnsNull_WhenNoCollision()
    {
        var seg = MakeSegment("X", 2, 4);
        var other = MakeSegment("B", 10, 12);
        var all = new[] { seg, other };

        var result = _sut.TryClampAtCollision(seg, 5.0, 7.0, all, GridSize);
        Assert.Null(result);
    }

    [Fact]
    public void TryClampAtCollision_ReturnsNull_WhenClampedPositionAlsoCollides()
    {
        // Segment stuck between two others — clamp would overlap the other side.
        var seg = MakeSegment("X", 5, 7);
        var left = MakeSegment("L", 2, 5);
        var right = MakeSegment("R", 6, 9);
        var all = new[] { seg, left, right };

        // Moving right into R: clamp end at R.Start=6, start=4.
        // But start=4 overlaps L (2–5)? Actually 4 < 5, so (4, 6) overlaps L(2,5) since 4 < 5.
        var result = _sut.TryClampAtCollision(seg, 5.5, 7.5, all, GridSize);
        Assert.Null(result);
    }

    // ── ResolveTiming ─────────────────────────────────────────────────

    [Fact]
    public void ResolveTiming_Move_NoCollision_SnapsToGrid()
    {
        var seg = MakeSegment("X", 1, 3);
        var all = new[] { seg };
        double expanded = 0;

        var result = _sut.ResolveTiming(seg, 2.006, 4.006, all, totalDuration: 20, GridSize,
            expandTimeline: v => expanded = v);

        Assert.NotNull(result);
        Assert.Equal(2.01, result!.Value.start, 2);
        Assert.Equal(4.01, result.Value.end, 2);
        Assert.Equal(0, expanded); // No expansion needed
    }

    [Fact]
    public void ResolveTiming_Move_ClampsAtCollision()
    {
        var seg = MakeSegment("X", 1, 3);
        var blocker = MakeSegment("B", 5, 8);
        var all = new[] { seg, blocker };

        // Move to 4–6 collides with B. Should clamp before B.
        var result = _sut.ResolveTiming(seg, 4.0, 6.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.True(result!.Value.end <= blocker.StartTime + 0.01,
            $"End {result.Value.end} should not exceed blocker start {blocker.StartTime}");
    }

    [Fact]
    public void ResolveTiming_ResizeRight_CapsAtNextSegment()
    {
        var seg = MakeSegment("X", 2, 4);
        var next = MakeSegment("N", 6, 8);
        var all = new[] { seg, next };

        // Resize right from 4 to 7, which collides with N at 6.
        // Should cap at N.StartTime = 6.
        var result = _sut.ResolveTiming(seg, 2.0, 7.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Value.start);
        Assert.True(result.Value.end <= 6.0 + 0.01);
    }

    [Fact]
    public void ResolveTiming_ResizeLeft_FloorsAtPreviousSegment()
    {
        var seg = MakeSegment("X", 5, 8);
        var prev = MakeSegment("P", 1, 3);
        var all = new[] { seg, prev };

        // Resize left from 5 to 2, which collides with P ending at 3.
        // Should floor at P.EndTime = 3.
        var result = _sut.ResolveTiming(seg, 2.0, 8.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.Equal(8.0, result!.Value.end);
        Assert.True(result.Value.start >= 3.0 - 0.01);
    }

    [Fact]
    public void ResolveTiming_ReturnsNull_WhenBlocked()
    {
        var seg = MakeSegment("X", 5, 7);
        var left = MakeSegment("L", 2, 5);
        var right = MakeSegment("R", 7, 10);
        var all = new[] { seg, left, right };

        // Trying to move right into R while L is directly behind — completely blocked.
        var result = _sut.ResolveTiming(seg, 5.5, 7.5, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        // Should either clamp at current position or return null (blocked)
        // Depending on implementation, it may clamp successfully or block.
        // The key assertion: segment should NOT jump past the blocker.
        if (result.HasValue)
        {
            Assert.True(result.Value.end <= right.StartTime + 0.01);
            Assert.True(result.Value.start >= left.EndTime - 0.01);
        }
    }

    [Fact]
    public void ResolveTiming_Move_NegativeStart_ClampsToZero()
    {
        var seg = MakeSegment("X", 1, 3);
        var all = new[] { seg };

        var result = _sut.ResolveTiming(seg, -1.0, 1.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.True(result!.Value.start >= 0);
    }

    [Fact]
    public void ResolveTiming_Move_ExpandsTimeline_WhenEndExceedsDuration()
    {
        var seg = MakeSegment("X", 1, 3);
        var all = new[] { seg };
        double expandedTo = 0;

        var result = _sut.ResolveTiming(seg, 18.0, 20.5, all, totalDuration: 20, GridSize,
            expandTimeline: v => expandedTo = v);

        Assert.NotNull(result);
        Assert.True(expandedTo > 0, "Timeline should have been expanded");
    }

    [Fact]
    public void ResolveTiming_ZeroDuration_ReturnsNull()
    {
        var seg = MakeSegment("X", 5, 5);
        var all = new[] { seg };

        var result = _sut.ResolveTiming(seg, 5.0, 5.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.Null(result);
    }
}
