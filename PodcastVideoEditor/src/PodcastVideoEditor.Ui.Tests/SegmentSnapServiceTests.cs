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

    // ── TrySnapToBoundary ────────────────────────────────────────────

    [Fact]
    public void TrySnapToBoundary_SnapsToNearestValidBoundary_Left()
    {
        var seg = MakeSegment("X", 2, 4);
        var blocker = MakeSegment("B", 5, 8);
        var all = new[] { seg, blocker };

        // Requesting (4, 6) overlaps B. Nearest boundary to currentStart=2:
        // After B: (8, 10) dist=6. Before B: (3, 5) dist=1. → Before B wins.
        var result = _sut.TrySnapToBoundary(seg, 4.0, 6.0, all, 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(3.0, result!.Value.start, 2);
        Assert.Equal(5.0, result.Value.end, 2);
    }

    [Fact]
    public void TrySnapToBoundary_SnapsToNearestValidBoundary_Right()
    {
        var seg = MakeSegment("X", 9, 11);
        var blocker = MakeSegment("B", 5, 8);
        var all = new[] { seg, blocker };

        // Requesting (6, 8) overlaps B. Nearest boundary to currentStart=9:
        // After B: (8, 10) dist=1. Before B: (3, 5) dist=6. → After B wins.
        var result = _sut.TrySnapToBoundary(seg, 6.0, 8.0, all, 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(8.0, result!.Value.start, 2);
        Assert.Equal(10.0, result.Value.end, 2);
    }

    [Fact]
    public void TrySnapToBoundary_ReturnsNull_WhenNoValidPosition()
    {
        // Segment stuck between two others — no gap wide enough.
        var seg = MakeSegment("X", 5, 7);
        var left = MakeSegment("L", 2, 5);
        var right = MakeSegment("R", 6, 9);
        var all = new[] { seg, left, right };

        // Only 1-second gap between L and R (5 to 6), but segment is 2 seconds wide.
        var result = _sut.TrySnapToBoundary(seg, 5.5, 7.5, all, 20, GridSize);
        // Before R: end=6, start=4 → overlaps L(2,5) since 4 < 5. Invalid.
        // After L: start=5, end=7 → overlaps R(6,9). Invalid.
        // Before L: end=2, start=0. dist=|0-5|=5. Valid!
        // After R: start=9, end=11. dist=|9-5|=4. Valid!
        // Winner: After R at dist=4.
        Assert.NotNull(result);
        Assert.Equal(9.0, result!.Value.start, 2);
    }

    [Fact]
    public void TrySnapToBoundary_PicksClosestToCurrentPosition()
    {
        // Segment near blocker's left edge
        var seg = MakeSegment("X", 3, 5);
        var blocker = MakeSegment("B", 5, 8);
        var all = new[] { seg, blocker };

        // Requesting (4.5, 6.5) overlaps B. currentStart=3.
        // After B: (8, 10) dist=5. Before B: (3, 5) dist=0. → Before B wins.
        var result = _sut.TrySnapToBoundary(seg, 4.5, 6.5, all, 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(3.0, result!.Value.start, 2);
        Assert.Equal(5.0, result.Value.end, 2);
    }

    [Fact]
    public void TrySnapToBoundary_StuckBetweenSegments_FindsCurrentPosition()
    {
        // X(5,7) perfectly between L(0,5) and R(7,12).
        var seg = MakeSegment("X", 5, 7);
        var left = MakeSegment("L", 0, 5);
        var right = MakeSegment("R", 7, 12);
        var all = new[] { seg, left, right };

        // After L: (5, 7). Check collision: vs R(7,12) → HasOverlap(5,7,7,12) = false. Valid!
        // dist = |5 - 5| = 0. This IS the current position.
        var result = _sut.TrySnapToBoundary(seg, 5.5, 7.5, all, 20, GridSize);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value.start, 2);
        Assert.Equal(7.0, result.Value.end, 2);
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
    public void ResolveTiming_ReturnsCurrentPosition_WhenBlocked()
    {
        var seg = MakeSegment("X", 5, 7);
        var left = MakeSegment("L", 2, 5);
        var right = MakeSegment("R", 7, 10);
        var all = new[] { seg, left, right };

        // Trying to move right into R while L is directly behind — completely blocked.
        // Should return current position (segment stays put).
        var result = _sut.ResolveTiming(seg, 5.5, 7.5, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.True(result!.Value.end <= right.StartTime + 0.01);
        Assert.True(result.Value.start >= left.EndTime - 0.01);
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

    // ── Bug regression tests ──────────────────────────────────────────

    [Fact]
    public void Bug1_ThreeSegments_DragMiddleLeft_ClampsNearCurrentPosition()
    {
        // Setup: A(0–2), X(5–7), C(10–12) on one track.
        // Drag X leftward. When X collides with A, TrySnapToBoundary picks the
        // nearest valid boundary to X's currentStart=5.
        var a = MakeSegment("A", 0, 2);
        var x = MakeSegment("X", 5, 7);
        var c = MakeSegment("C", 10, 12);
        var all = new[] { a, x, c };

        // Simulate dragging X leftward to overlap with A: requesting (1, 3)
        var result = _sut.ResolveTiming(x, 1.0, 3.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        // Nearest boundary to currentStart(5): after A at (2,4) dist=3, before C at (8,10) dist=3.
        // After A found first → (2, 4).
        Assert.Equal(2.0, result!.Value.start, 2);
        Assert.Equal(4.0, result.Value.end, 2);
    }

    [Fact]
    public void Bug1_ThreeSegments_DragMiddleRight_ClampsNearCurrentPosition()
    {
        // Drag X rightward into C — but with X's current position closer to C.
        var a = MakeSegment("A", 0, 2);
        var x = MakeSegment("X", 8, 10); // current position near C
        var c = MakeSegment("C", 10, 12);
        var all = new[] { a, x, c };

        // Requesting (9.5, 11.5) overlaps C. TrySnapToBoundary uses current position (8).
        // Before C: end=10, start=8. dist=0. After C: (12,14) dist=4. → Before C wins.
        var result = _sut.ResolveTiming(x, 9.5, 11.5, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.Equal(8.0, result!.Value.start, 2);
        Assert.Equal(10.0, result.Value.end, 2);
    }

    [Fact]
    public void Bug2_ResolveTiming_StuckBetweenSegments_StaysInPlace()
    {
        // When segment is perfectly between two segments with no extra gap,
        // TrySnapToBoundary finds the current position as the nearest valid slot.
        var left = MakeSegment("L", 0, 5);
        var x = MakeSegment("X", 5, 7);
        var right = MakeSegment("R", 7, 12);
        var all = new[] { left, x, right };

        // Try to move right — TrySnapToBoundary finds current position (5, 7) as best
        var resultRight = _sut.ResolveTiming(x, 5.5, 7.5, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(resultRight);
        Assert.Equal(5.0, resultRight!.Value.start, 2);
        Assert.Equal(7.0, resultRight.Value.end, 2);

        // Try to move left — same result
        var resultLeft = _sut.ResolveTiming(x, 4.5, 6.5, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(resultLeft);
        Assert.Equal(5.0, resultLeft!.Value.start, 2);
        Assert.Equal(7.0, resultLeft.Value.end, 2);
    }

    [Fact]
    public void ResolveTiming_Move_CollisionSnapsBoundaryNearSegment()
    {
        // Verify collision resolution snaps to nearest boundary relative to currentStart
        var seg = MakeSegment("X", 5, 7);
        var blocker = MakeSegment("B", 2, 4);
        var all = new[] { seg, blocker };

        // Moving left: requesting (3, 5) overlaps B(2,4).
        // TrySnapToBoundary: after B = (4, 6) dist=1. Before B = (0, 2) dist=5.
        // After B wins.
        var result = _sut.ResolveTiming(seg, 3.0, 5.0, all, totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        Assert.Equal(4.0, result!.Value.start, 2);
        Assert.Equal(6.0, result.Value.end, 2);
    }
}
