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

    /// <summary>Snap-in threshold in seconds (engage magnetic snap).</summary>
    public double SnapThreshold => FrozenPPS > 0 ? TimelineConstants.SnapPixelThreshold / FrozenPPS : 0.1;

    /// <summary>Snap-release threshold in seconds (disengage magnetic snap — larger to add hysteresis).</summary>
    public double SnapReleaseThreshold => FrozenPPS > 0 ? TimelineConstants.SnapReleasePixelThreshold / FrozenPPS : 0.15;

    /// <summary>Whether the segment is currently held by magnetic snap.</summary>
    private bool _isSnapped;

    /// <summary>The edge time we are currently snapped to (for hysteresis release check).</summary>
    private double _snappedEdgeTime;

    /// <summary>Whether a resize edge is currently held by cross-track magnetic snap.</summary>
    private bool _resizeIsSnapped;

    private double PixelsToTime(double px) => FrozenPPS > 0 ? px / FrozenPPS : 0;

    /// <summary>
    /// Return the effective snap threshold for resize, applying hysteresis.
    /// </summary>
    public double ResizeSnapThreshold => _resizeIsSnapped ? SnapReleaseThreshold : SnapThreshold;

    /// <summary>
    /// Update resize snap hysteresis state after a cross-track snap attempt.
    /// </summary>
    public void UpdateResizeSnapState(bool didSnap)
    {
        _resizeIsSnapped = didSnap;
    }

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
    /// Apply magnetic snap for a move operation. Returns snapped (newStart, newEnd)
    /// plus the edge time to show the snap indicator at (null if no snap).
    /// </summary>
    public (double newStart, double newEnd, double? snapEdgeTime) ApplyMoveSnap(
        double newStart, double newEnd, TimelineViewModel vm)
    {
        const double epsilon = 0.0005;
        double duration = _baselineEndTime - _baselineStartTime;

        // Use wider threshold when already snapped (hysteresis prevents jitter at boundary)
        double threshold = _isSnapped ? SnapReleaseThreshold : SnapThreshold;

        double snappedByStart = vm.SnapToAllTrackEdges(newStart, Segment.Id, threshold);
        double snappedByEnd = vm.SnapToAllTrackEdges(newEnd, Segment.Id, threshold);
        double distS = Math.Abs(snappedByStart - newStart);
        double distE = Math.Abs(snappedByEnd - newEnd);

        bool startSnapped = distS > epsilon && distS < threshold;
        bool endSnapped = distE > epsilon && distE < threshold;

        if (startSnapped && endSnapped)
        {
            if (distS <= distE)
            {
                newStart = snappedByStart;
                newEnd = newStart + duration;
                _isSnapped = true;
                _snappedEdgeTime = snappedByStart;
                return (newStart, newEnd, snappedByStart);
            }
            else
            {
                newEnd = snappedByEnd;
                newStart = newEnd - duration;
                _isSnapped = true;
                _snappedEdgeTime = snappedByEnd;
                return (newStart, newEnd, snappedByEnd);
            }
        }
        else if (startSnapped)
        {
            newStart = snappedByStart;
            newEnd = newStart + duration;
            _isSnapped = true;
            _snappedEdgeTime = snappedByStart;
            return (newStart, newEnd, snappedByStart);
        }
        else if (endSnapped)
        {
            newEnd = snappedByEnd;
            newStart = newEnd - duration;
            _isSnapped = true;
            _snappedEdgeTime = snappedByEnd;
            return (newStart, newEnd, snappedByEnd);
        }

        // No snap — clear hysteresis state
        _isSnapped = false;
        return (newStart, newEnd, null);
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

    /// <summary>
    /// Call after UpdateSegmentTiming succeeds for a resize operation.
    /// If the applied edge differs from the proposed value (collision clamped it),
    /// resets the baseline to the actual position to prevent accumulator drift.
    /// </summary>
    public void HandleResizeSnapCorrection(DragKind kind)
    {
        if (kind == DragKind.ResizeRight)
        {
            if (Math.Abs(Segment.EndTime - _baselineEndTime - PixelsToTime(_accumulatorDeltaX)) > BaselineTolerance)
            {
                _baselineEndTime = Segment.EndTime;
                _accumulatorDeltaX = 0;
            }
        }
        else if (kind == DragKind.ResizeLeft)
        {
            if (Math.Abs(Segment.StartTime - _baselineStartTime - PixelsToTime(_accumulatorDeltaX)) > BaselineTolerance)
            {
                _baselineStartTime = Segment.StartTime;
                _accumulatorDeltaX = 0;
            }
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
