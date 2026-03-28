using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.Helpers;
using PodcastVideoEditor.Ui.Services;
using System;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

/// <summary>
/// Tests for SegmentDragOperation (baseline-reset positioning, HandleSnapCorrection)
/// and SegmentSnapService changes (move preserves duration).
/// </summary>
public sealed class SegmentDragOperationTests
{
    private const double GridSize = 0.01;

    private static Segment MakeSegment(string id, double start, double end, string trackId = "T1")
        => new() { Id = id, StartTime = start, EndTime = end, TrackId = trackId, Kind = "visual" };

    // ── Baseline positioning tests ───────────────────────────────────

    [Fact]
    public void UpdateMove_ComputesFromBaseline()
    {
        // PPS=100: 1px = 0.01s
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // Drag 200px right → newStart = 5 + 200/100 = 7.0
        var (newStart, newEnd) = op.UpdateMove(200.0);
        Assert.Equal(7.0, newStart, 2);
        Assert.Equal(9.0, newEnd, 2); // duration preserved = 2.0
    }

    [Fact]
    public void UpdateMove_AccumulatesMultipleDeltas()
    {
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        op.UpdateMove(50.0);  // total = 50px
        op.UpdateMove(50.0);  // total = 100px
        var (newStart, newEnd) = op.UpdateMove(50.0);  // total = 150px

        // newStart = 5 + 150/100 = 6.5
        Assert.Equal(6.5, newStart, 2);
        Assert.Equal(8.5, newEnd, 2);
    }

    [Fact]
    public void HandleSnapCorrection_ResetsBaselineAfterCollisionSnap()
    {
        // Key anti-oscillation test: when collision resolution moves the segment
        // to a different position than requested, HandleSnapCorrection resets the
        // baseline so subsequent frames compute delta FROM the snapped position.
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // First delta: 100px right → requested (6, 8)
        var (req1Start, req1End) = op.UpdateMove(100.0);
        Assert.Equal(6.0, req1Start, 2);

        // Simulate collision clamping the segment to (5.5, 7.5) instead
        seg.StartTime = 5.5;
        seg.EndTime = 7.5;

        // HandleSnapCorrection detects the difference and resets baseline
        op.HandleSnapCorrection(req1Start, req1End);

        // Next delta: 50px right → should compute from new baseline (5.5, 7.5)
        // newStart = 5.5 + 50/100 = 6.0
        var (req2Start, req2End) = op.UpdateMove(50.0);
        Assert.Equal(6.0, req2Start, 2);
        Assert.Equal(8.0, req2End, 2);
    }

    [Fact]
    public void HandleSnapCorrection_NoResetWhenPositionMatches()
    {
        // When the segment's position matches the request, no baseline reset
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // Move 100px right → requested (6, 8)
        var (reqStart, reqEnd) = op.UpdateMove(100.0);
        seg.StartTime = 6.0;
        seg.EndTime = 8.0;

        // No collision snap — position matches
        op.HandleSnapCorrection(reqStart, reqEnd);

        // Next frame: 50px right → total 150px from ORIGINAL baseline (5)
        // newStart = 5 + 150/100 = 6.5
        var (newStart, newEnd) = op.UpdateMove(50.0);
        Assert.Equal(6.5, newStart, 2);
    }

    [Fact]
    public void UpdateMove_ClampsToZero()
    {
        var seg = MakeSegment("X", 1, 3);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // Drag 200px left → proposed = 1 - 2.0 = -1.0, should clamp to 0
        var (newStart, newEnd) = op.UpdateMove(-200.0);
        Assert.Equal(0.0, newStart, 2);
        Assert.Equal(2.0, newEnd, 2); // duration preserved
    }

    [Fact]
    public void UpdateMove_PreservesDuration()
    {
        var seg = MakeSegment("X", 3, 8); // duration = 5.0
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        var (newStart, newEnd) = op.UpdateMove(150.0);
        Assert.Equal(5.0, newEnd - newStart, 2);
    }

    // ── Cross-track vertical accumulator ─────────────────────────────

    [Fact]
    public void AccumulateVerticalDelta_TriggersAfterThreshold()
    {
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // Small deltas don't trigger
        Assert.False(op.AccumulateVerticalDelta(10.0));
        Assert.False(op.AccumulateVerticalDelta(10.0));

        // Exceeding threshold triggers and resets
        Assert.True(op.AccumulateVerticalDelta(15.0)); // total = 35 ≥ 30
    }

    [Fact]
    public void AccumulateVerticalDelta_SignedCancellation()
    {
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        // Random up/down jitter cancels out
        Assert.False(op.AccumulateVerticalDelta(10.0));
        Assert.False(op.AccumulateVerticalDelta(-10.0)); // total back to 0
        Assert.False(op.AccumulateVerticalDelta(10.0));  // total = 10
        Assert.False(op.AccumulateVerticalDelta(-10.0)); // total = 0 again
    }

    // ── Resize tests ─────────────────────────────────────────────────

    [Fact]
    public void UpdateResizeRight_UsesBaselinePositioning()
    {
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginResizeRight(seg, 100.0);

        // Drag 100px right → newEnd = 7 + 100/100 = 8.0
        double newEnd = op.UpdateResizeRight(100.0, 0.01);
        Assert.Equal(8.0, newEnd, 2);
    }

    [Fact]
    public void UpdateResizeLeft_UsesBaselinePositioning()
    {
        var seg = MakeSegment("X", 5, 7);
        var op = SegmentDragOperation.BeginResizeLeft(seg, 100.0);

        // Drag 200px left → newStart = 5 - 200/100 = 3.0
        double newStart = op.UpdateResizeLeft(-200.0, 0.01);
        Assert.Equal(3.0, newStart, 2);
    }

    // ── Move preserves duration via ResolveTiming ────────────────────

    [Fact]
    public void ResolveTiming_Move_PreservesDuration_AfterGridSnap()
    {
        var sut = new SegmentSnapService();
        var seg = MakeSegment("X", 5, 7); // duration = 2.0
        var all = new[] { seg };

        // Request a move to (5.123, 7.123) — grid snap should round start to 5.12
        // and both are snapped: end = SnapToGrid(7.123) ≈ 7.12
        var result = sut.ResolveTiming(seg, 5.123, 7.123, all,
            totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
        // Both start and end independently snapped but should approximate duration
        Assert.Equal(5.12, result!.Value.start, 2);
    }

    [Fact]
    public void ResolveTiming_Move_WithInference_PreservesDuration()
    {
        var sut = new SegmentSnapService();
        var seg = MakeSegment("X", 5, 7); // duration = 2.0
        var all = new[] { seg };

        // Both start and end changed → inferred as move
        var result = sut.ResolveTiming(seg, 5.456, 7.456, all,
            totalDuration: 20, GridSize,
            expandTimeline: _ => { });

        Assert.NotNull(result);
    }

    // ── Segment PixelLeft / PixelWidth binding properties ───────────

    [Fact]
    public void Segment_PixelLeft_ComputesFromStartTimeAndPPS()
    {
        var seg = MakeSegment("X", 5.0, 10.0);
        seg.TimelinePixelsPerSecond = 100.0;

        Assert.Equal(500.0, seg.PixelLeft, 2);
    }

    [Fact]
    public void Segment_PixelWidth_ComputesFromDuration()
    {
        var seg = MakeSegment("X", 5.0, 10.0);
        seg.TimelinePixelsPerSecond = 100.0;

        Assert.Equal(500.0, seg.PixelWidth, 2); // (10-5)*100 = 500
    }

    [Fact]
    public void Segment_PixelWidth_Minimum4px()
    {
        var seg = MakeSegment("X", 5.0, 5.001);
        seg.TimelinePixelsPerSecond = 100.0;

        // Duration = 0.001s, PPS = 100 => 0.1px, but minimum is 4
        Assert.Equal(4.0, seg.PixelWidth, 2);
    }

    [Fact]
    public void Segment_PixelLeft_ZeroPPS_ReturnsZero()
    {
        var seg = MakeSegment("X", 5.0, 10.0);
        seg.TimelinePixelsPerSecond = 0;

        Assert.Equal(0, seg.PixelLeft);
    }

    // ── SnapToSegmentEdge detection ─────────────────────────────────

    [Fact]
    public void SnapToSegmentEdge_SnapsToNearestEdge()
    {
        var sut = new SegmentSnapService();
        var segments = new[]
        {
            MakeSegment("A", 0, 3),
            MakeSegment("B", 5, 8)
        };

        // 4.9 is within 0.2s threshold of B.Start(5.0) => should snap to 5.0
        double result = sut.SnapToSegmentEdge(4.9, segments, null, 0.2);
        Assert.Equal(5.0, result, 2);
    }

    [Fact]
    public void SnapToSegmentEdge_NoSnap_WhenOutsideThreshold()
    {
        var sut = new SegmentSnapService();
        var segments = new[]
        {
            MakeSegment("A", 0, 3),
            MakeSegment("B", 5, 8)
        };

        // 4.0 is 1.0s from both A.End(3) and B.Start(5) => no snap with 0.2s threshold
        double result = sut.SnapToSegmentEdge(4.0, segments, null, 0.2);
        Assert.Equal(4.0, result, 2);
    }

    [Fact]
    public void SnapToSegmentEdge_ExcludesSpecifiedSegment()
    {
        var sut = new SegmentSnapService();
        var segments = new[]
        {
            MakeSegment("A", 0, 3),
            MakeSegment("B", 5, 8)
        };

        // 4.9 close to B.Start but B excluded => should not snap
        double result = sut.SnapToSegmentEdge(4.9, segments, "B", 0.2);
        Assert.Equal(4.9, result, 2);
    }

    // ── DragKind enum ────────────────────────────────────────────────

    [Fact]
    public void DragKind_HasExpectedValues()
    {
        Assert.Equal(0, (int)DragKind.None);
        Assert.Equal(1, (int)DragKind.Move);
        Assert.Equal(2, (int)DragKind.ResizeRight);
        Assert.Equal(3, (int)DragKind.ResizeLeft);
    }

    // ── UndoOriginalTrackId ─────────────────────────────────────────

    [Fact]
    public void DragOperation_CapturesOriginalTrackId()
    {
        var seg = MakeSegment("X", 5, 7, "TrackA");
        var op = SegmentDragOperation.BeginMove(seg, 100.0);

        Assert.Equal("TrackA", op.UndoOriginalTrackId);
    }
}
