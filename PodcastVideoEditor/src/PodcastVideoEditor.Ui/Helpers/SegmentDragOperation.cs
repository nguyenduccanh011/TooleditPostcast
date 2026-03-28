using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.ViewModels;
using System;

namespace PodcastVideoEditor.Ui.Helpers;

/// <summary>
/// The kind of segment drag interaction in progress.
/// </summary>
public enum DragKind
{
    None,
    Move,
    ResizeRight,
    ResizeLeft
}

/// <summary>
/// Encapsulates all mutable state for a single segment drag/resize/move interaction.
/// Uses baseline-reset pattern: when collision resolution moves the segment to a
/// different position than requested, the baseline is reset to that position and
/// the pixel accumulator is zeroed. This prevents the accumulator from "fighting"
/// against collision snaps (the root cause of oscillation).
/// </summary>
internal sealed class SegmentDragOperation
{
    private const double BaselineTolerance = 0.01;

    // ── Immutable state (set at construction) ──────────────────────────────
    public DragKind Kind { get; }
    public Segment Segment { get; }

    /// <summary>Frozen PixelsPerSecond at drag start — all conversions use this.</summary>
    public double FrozenPPS { get; }

    // ── Undo snapshots (never change mid-drag) ─────────────────────────────
    public double UndoOriginalStart { get; }
    public double UndoOriginalEnd { get; }
    public double UndoOriginalSourceOffset { get; }
    public string UndoOriginalTrackId { get; } = string.Empty;

    // ── Mutable baseline state (reset when collision-snap detected) ────────
    private double _baselineStartTime;
    private double _baselineEndTime;
    private double _accumulatorDeltaX;

    /// <summary>
    /// Accumulated vertical pixel delta since the last track-switch event.
    /// Cross-track switching only fires when this exceeds <see cref="CrossTrackThresholdPx"/>.
    /// </summary>
    private double _totalVerticalDeltaPx;

    /// <summary>
    /// Minimum accumulated vertical drag distance (px) required to trigger a cross-track move.
    /// </summary>
    public const double CrossTrackThresholdPx = 30.0;

    private SegmentDragOperation(DragKind kind, Segment segment, double frozenPPS)
    {
        Kind = kind;
        Segment = segment;
        FrozenPPS = frozenPPS;

        UndoOriginalStart = segment.StartTime;
        UndoOriginalEnd = segment.EndTime;
        UndoOriginalSourceOffset = segment.SourceStartOffset;
        UndoOriginalTrackId = segment.TrackId ?? string.Empty;

        _baselineStartTime = segment.StartTime;
        _baselineEndTime = segment.EndTime;
        _accumulatorDeltaX = 0;
    }

    // ── Factory methods ────────────────────────────────────────────────────

    public static SegmentDragOperation BeginMove(Segment segment, double frozenPPS)
        => new(DragKind.Move, segment, frozenPPS);

    public static SegmentDragOperation BeginResizeRight(Segment segment, double frozenPPS)
        => new(DragKind.ResizeRight, segment, frozenPPS);

    public static SegmentDragOperation BeginResizeLeft(Segment segment, double frozenPPS)
        => new(DragKind.ResizeLeft, segment, frozenPPS);

    // ── Conversion helpers ─────────────────────────────────────────────────

    /// <summary>Snap threshold in seconds (constant 15px converted via frozen PPS).</summary>
    public double SnapThreshold => FrozenPPS > 0 ? TimelineConstants.SnapPixelThreshold / FrozenPPS : 0.1;

    private double PixelsToTime(double px) => FrozenPPS > 0 ? px / FrozenPPS : 0;

    // ── Cross-track vertical accumulator ─────────────────────────────────────

    /// <summary>
    /// Accumulate a vertical drag delta and return <see langword="true"/> when the
    /// signed total has exceeded <see cref="CrossTrackThresholdPx"/> in either direction.
    /// Resets the accumulator after firing.
    /// </summary>
    public bool AccumulateVerticalDelta(double delta)
    {
        _totalVerticalDeltaPx += delta;
        if (Math.Abs(_totalVerticalDeltaPx) >= CrossTrackThresholdPx)
        {
            _totalVerticalDeltaPx = 0;
            return true;
        }
        return false;
    }

    // ── Move delta ─────────────────────────────────────────────────────────

    /// <summary>
    /// Process a move drag delta. Returns proposed (newStart, newEnd) preserving original duration.
    /// Uses baseline pattern: newStart = baselineStartTime + accumulatedDelta / frozenPPS.
    /// Call <see cref="HandleSnapCorrection"/> after UpdateSegmentTiming to reset baseline
    /// when collision resolution changes the position.
    /// </summary>
    public (double newStart, double newEnd) UpdateMove(double horizontalChange)
    {
        _accumulatorDeltaX += horizontalChange;
        double timeDelta = PixelsToTime(_accumulatorDeltaX);
        double duration = _baselineEndTime - _baselineStartTime;
        double newStart = _baselineStartTime + timeDelta;
        double newEnd = newStart + duration;

        if (newStart < 0)
        {
            newStart = 0;
            newEnd = duration;
            _accumulatorDeltaX = (newStart - _baselineStartTime) * FrozenPPS;
        }

        return (newStart, newEnd);
    }

    /// <summary>
    /// Apply magnetic snap for a move operation. Returns snapped (newStart, newEnd).
    /// </summary>
    public (double newStart, double newEnd) ApplyMoveSnap(
        double newStart, double newEnd, TimelineViewModel vm)
    {
        double duration = _baselineEndTime - _baselineStartTime;
        double threshold = SnapThreshold;

        double snappedByStart = vm.SnapToSegmentEdge(newStart, Segment.TrackId, Segment.Id, threshold);
        double snappedByEnd = vm.SnapToSegmentEdge(newEnd, Segment.TrackId, Segment.Id, threshold);
        double distS = Math.Abs(snappedByStart - newStart);
        double distE = Math.Abs(snappedByEnd - newEnd);

        if (distS <= distE && distS < threshold)
        {
            newStart = snappedByStart;
            newEnd = newStart + duration;
        }
        else if (distE < threshold)
        {
            newEnd = snappedByEnd;
            newStart = newEnd - duration;
        }

        return (newStart, newEnd);
    }

    // ── Snap/collision correction ──────────────────────────────────────────

    /// <summary>
    /// Call after UpdateSegmentTiming succeeds. If the applied position differs from
    /// requested (collision snap occurred), resets the baseline to prevent oscillation.
    /// This is the KEY anti-oscillation mechanism: when the segment is clamped to a
    /// boundary, the baseline is reset to that boundary position, so subsequent frames
    /// compute delta FROM the boundary rather than fighting against it.
    /// </summary>
    public void HandleSnapCorrection(double requestedStart, double requestedEnd)
    {
        bool startDiffers = Math.Abs(Segment.StartTime - requestedStart) > BaselineTolerance;
        bool endDiffers = Math.Abs(Segment.EndTime - requestedEnd) > BaselineTolerance;

        if (startDiffers || endDiffers)
        {
            _baselineStartTime = Segment.StartTime;
            _baselineEndTime = Segment.EndTime;
            _accumulatorDeltaX = 0;
        }
    }

    // ── Resize-right delta ─────────────────────────────────────────────────

    /// <summary>
    /// Process a resize-right drag delta. Returns proposed newEndTime.
    /// </summary>
    public double UpdateResizeRight(double horizontalChange, double gridSize)
    {
        _accumulatorDeltaX += horizontalChange;
        double timeDelta = PixelsToTime(_accumulatorDeltaX);
        double newEnd = _baselineEndTime + timeDelta;

        if (newEnd <= Segment.StartTime)
            newEnd = Segment.StartTime + gridSize;

        return newEnd;
    }

    // ── Resize-left delta ──────────────────────────────────────────────────

    /// <summary>
    /// Process a resize-left drag delta. Returns proposed newStartTime.
    /// </summary>
    public double UpdateResizeLeft(double horizontalChange, double gridSize)
    {
        _accumulatorDeltaX += horizontalChange;
        double timeDelta = PixelsToTime(_accumulatorDeltaX);
        double newStart = _baselineStartTime + timeDelta;

        if (newStart < 0) newStart = 0;
        if (newStart >= Segment.EndTime) newStart = Segment.EndTime - gridSize;

        return newStart;
    }

    /// <summary>
    /// Clamp startTime for audio segments so SourceStartOffset cannot go negative.
    /// </summary>
    public double ClampForAudioLeft(double newStartTime)
    {
        double proposedOffsetDelta = newStartTime - UndoOriginalStart;
        double proposedOffset = UndoOriginalSourceOffset + proposedOffsetDelta;
        if (proposedOffset < 0)
            return UndoOriginalStart - UndoOriginalSourceOffset;
        return newStartTime;
    }

    /// <summary>
    /// Update SourceStartOffset after a successful left-resize for audio segments.
    /// </summary>
    public void UpdateAudioSourceOffset()
    {
        double offsetDelta = Segment.StartTime - UndoOriginalStart;
        Segment.SourceStartOffset = Math.Max(0, UndoOriginalSourceOffset + offsetDelta);
    }

    // ── Completion ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build the undo action if timing actually changed. Returns null if no change.
    /// </summary>
    public SegmentTimingChangedAction? BuildUndoAction(Action invalidateCache)
    {
        bool startChanged = Math.Abs(Segment.StartTime - UndoOriginalStart) > TimelineConstants.UndoTolerance;
        bool endChanged = Math.Abs(Segment.EndTime - UndoOriginalEnd) > TimelineConstants.UndoTolerance;

        if (!startChanged && !endChanged)
            return null;

        if (Kind == DragKind.ResizeLeft)
        {
            return new SegmentTimingChangedAction(
                Segment,
                UndoOriginalStart, Segment.EndTime,
                Segment.StartTime, Segment.EndTime,
                UndoOriginalSourceOffset, Segment.SourceStartOffset,
                invalidateCache);
        }

        if (Kind == DragKind.ResizeRight)
        {
            return new SegmentTimingChangedAction(
                Segment,
                Segment.StartTime, UndoOriginalEnd,
                Segment.StartTime, Segment.EndTime,
                invalidateCache);
        }

        // Move
        return new SegmentTimingChangedAction(
            Segment,
            UndoOriginalStart, UndoOriginalEnd,
            Segment.StartTime, Segment.EndTime,
            invalidateCache);
    }
}
