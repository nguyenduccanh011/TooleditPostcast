using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Helpers;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Default implementation of <see cref="ISegmentDragHandler"/>.
/// Consolidates all drag processing logic that was previously scattered
/// across TimelineView.xaml.cs code-behind methods.
/// </summary>
internal sealed class SegmentDragHandler : ISegmentDragHandler
{
    private readonly TimelineViewModel _vm;
    private SegmentDragOperation? _dragOp;

    public SegmentDragHandler(TimelineViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public SegmentDragOperation? ActiveDrag => _dragOp;

    public void BeginMove(Segment segment, double frozenPPS)
    {
        _dragOp = SegmentDragOperation.BeginMove(segment, frozenPPS);
        _rippleDeltaAccumulator = 0;
        _vm.IsDeferringThumbnailUpdate = true;
        segment.IsDragging = true;
    }

    public void BeginResizeRight(Segment segment, double frozenPPS)
    {
        _dragOp = SegmentDragOperation.BeginResizeRight(segment, frozenPPS);
        _vm.IsDeferringThumbnailUpdate = true;
    }

    public void BeginResizeLeft(Segment segment, double frozenPPS)
    {
        _dragOp = SegmentDragOperation.BeginResizeLeft(segment, frozenPPS);
        _vm.IsDeferringThumbnailUpdate = true;
    }

    public DragResult ProcessDelta(double horizontalChange, bool ripple = false)
    {
        if (_dragOp == null)
            return new DragResult { Segment = null!, Updated = false };

        return _dragOp.Kind switch
        {
            DragKind.Move => ProcessMoveDelta(horizontalChange, ripple),
            DragKind.ResizeRight => ProcessResizeRightDelta(horizontalChange),
            DragKind.ResizeLeft => ProcessResizeLeftDelta(horizontalChange),
            _ => new DragResult { Segment = _dragOp.Segment, Updated = false }
        };
    }

    public IUndoableAction? CompleteDrag()
    {
        if (_dragOp == null)
            return null;

        var timingAction = _dragOp.BuildUndoAction(() => _vm.InvalidateActiveSegmentsCachePublic());

        // Collect all sub-actions to compound
        var actions = new List<IUndoableAction>();
        if (timingAction != null)
            actions.Add(timingAction);

        // Check if the segment was moved to a different track
        bool trackChanged = _dragOp.Segment.TrackId != _dragOp.UndoOriginalTrackId;
        if (trackChanged)
        {
            var segment = _dragOp.Segment;
            var originalTrackId = _dragOp.UndoOriginalTrackId;
            var newTrackId = segment.TrackId ?? string.Empty;

            actions.Add(new SegmentTrackChangedAction(segment, originalTrackId, newTrackId, _vm));
        }

        // Handle ripple shift undo: reverse the accumulated shift on neighbor segments
        if (Math.Abs(_rippleDeltaAccumulator) > 0.001)
        {
            var trackId = _dragOp.Segment.TrackId ?? _dragOp.UndoOriginalTrackId;
            var segId = _dragOp.Segment.Id;
            double totalDelta = _rippleDeltaAccumulator;
            var vm = _vm;

            actions.Add(new RippleShiftUndoAction(vm, trackId, segId, totalDelta));
        }

        IUndoableAction? finalAction = actions.Count switch
        {
            0 => null,
            1 => actions[0],
            _ => new CompoundAction("Move segment", actions)
        };

        _dragOp.Segment.IsDragging = false;
        _dragOp = null;
        _rippleDeltaAccumulator = 0;
        _vm.IsDeferringThumbnailUpdate = false;
        _vm.InvalidateActiveSegmentsCachePublic();
        _vm.RecalculateDurationFromSegments();
        return finalAction;
    }

    public void CancelDrag()
    {
        if (_dragOp == null) return;

        // Undo ripple shift
        if (Math.Abs(_rippleDeltaAccumulator) > 0.001)
        {
            var trackId = _dragOp.Segment.TrackId ?? _dragOp.UndoOriginalTrackId;
            var trackSegments = _vm.GetSegmentsForTrack(trackId);
            foreach (var seg in trackSegments)
            {
                if (seg.Id == _dragOp.Segment.Id) continue;
                if (seg.StartTime >= _dragOp.Segment.StartTime - _rippleDeltaAccumulator + 0.001)
                {
                    seg.StartTime -= _rippleDeltaAccumulator;
                    seg.EndTime -= _rippleDeltaAccumulator;
                }
            }
        }

        // Restore original track if changed
        if (_dragOp.Segment.TrackId != _dragOp.UndoOriginalTrackId)
        {
            var originalTrack = _vm.Tracks.FirstOrDefault(t => t.Id == _dragOp.UndoOriginalTrackId);
            if (originalTrack != null)
                _vm.MoveSegmentToTrack(_dragOp.Segment, originalTrack);
        }

        // Restore original timing
        _dragOp.Segment.StartTime = _dragOp.UndoOriginalStart;
        _dragOp.Segment.EndTime = _dragOp.UndoOriginalEnd;
        _dragOp.Segment.SourceStartOffset = _dragOp.UndoOriginalSourceOffset;
        _dragOp.Segment.IsDragging = false;

        _dragOp = null;
        _vm.IsDeferringThumbnailUpdate = false;
        _vm.InvalidateActiveSegmentsCachePublic();
    }

    public bool TryMoveToTrack(Track targetTrack)
    {
        if (_dragOp == null || _dragOp.Kind != DragKind.Move)
            return false;

        return _vm.MoveSegmentToTrack(_dragOp.Segment, targetTrack);
    }

    public bool AccumulateVerticalDelta(double verticalChange)
        => _dragOp?.AccumulateVerticalDelta(verticalChange) ?? false;

    // ── Private delta processors ─────────────────────────────────────────

    private DragResult ProcessMoveDelta(double horizontalChange, bool ripple = false)
    {
        var segment = _dragOp!.Segment;
        double oldStart = segment.StartTime;
        var (newStart, newEnd) = _dragOp.UpdateMove(horizontalChange);
        var (snappedStart, snappedEnd) = _dragOp.ApplyMoveSnap(newStart, newEnd, _vm);

        // Detect snap indicator
        double? snapTime = null;
        double snapDelta = Math.Abs(snappedStart - newStart);
        double snapDeltaEnd = Math.Abs(snappedEnd - newEnd);
        if (snapDelta > 0.001 || snapDeltaEnd > 0.001)
            snapTime = snapDelta <= snapDeltaEnd ? snappedStart : snappedEnd;

        bool updated = _vm.UpdateSegmentTiming(segment, snappedStart, snappedEnd);

        if (updated)
            _dragOp.HandleSnapCorrection(snappedStart, snappedEnd);

        // Ripple mode: shift all segments after this one by the same delta
        if (updated && ripple)
        {
            double rippleDelta = segment.StartTime - oldStart;
            if (Math.Abs(rippleDelta) > 0.001)
                ApplyRippleShift(segment, rippleDelta);
        }

        return new DragResult { Segment = segment, Updated = updated, SnapIndicatorTime = updated ? snapTime : null };
    }

    /// <summary>
    /// In ripple mode, shift all segments that start after the dragged segment
    /// by the given delta (positive = right, negative = left).
    /// Segments are moved directly without collision checks (they all shift together).
    /// </summary>
    private void ApplyRippleShift(Segment draggedSegment, double delta)
    {
        var trackSegments = _vm.GetSegmentsForTrack(draggedSegment.TrackId ?? string.Empty);
        foreach (var seg in trackSegments)
        {
            if (seg.Id == draggedSegment.Id) continue;
            // Only shift segments that were originally after the dragged segment
            if (seg.StartTime >= draggedSegment.StartTime - delta + 0.001)
            {
                double newStart = Math.Max(0, seg.StartTime + delta);
                double newEnd = seg.EndTime + delta;
                if (newEnd > newStart)
                {
                    seg.StartTime = newStart;
                    seg.EndTime = newEnd;
                }
            }
        }
        _rippleDeltaAccumulator += delta;
    }

    private double _rippleDeltaAccumulator;

    private DragResult ProcessResizeRightDelta(double horizontalChange)
    {
        var segment = _dragOp!.Segment;
        double newEndTime = _dragOp.UpdateResizeRight(horizontalChange, _vm.GridSize);

        // Audio segments: clamp to source file duration
        if (string.Equals(segment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
        {
            double? sourceDuration = _vm.GetSourceDurationForSegment(segment);
            if (sourceDuration.HasValue && sourceDuration.Value > 0)
            {
                double maxSegDuration = sourceDuration.Value - segment.SourceStartOffset;
                double maxEndTime = segment.StartTime + maxSegDuration;
                if (newEndTime > maxEndTime)
                    newEndTime = maxEndTime;
            }
        }

        // Magnetic snap to nearest segment edge
        double preSnapEnd = newEndTime;
        newEndTime = _vm.SnapToSegmentEdge(newEndTime, segment.TrackId, segment.Id, _dragOp.SnapThreshold);
        double? snapTime = Math.Abs(newEndTime - preSnapEnd) > 0.001 ? newEndTime : null;

        bool updated = _vm.UpdateSegmentTiming(segment, segment.StartTime, newEndTime);

        return new DragResult { Segment = segment, Updated = updated, SnapIndicatorTime = updated ? snapTime : null };
    }

    private DragResult ProcessResizeLeftDelta(double horizontalChange)
    {
        var segment = _dragOp!.Segment;
        double newStartTime = _dragOp.UpdateResizeLeft(horizontalChange, _vm.GridSize);

        // Audio segments: clamp so SourceStartOffset cannot go negative
        bool isAudio = string.Equals(segment.Kind, "audio", StringComparison.OrdinalIgnoreCase);
        if (isAudio)
            newStartTime = _dragOp.ClampForAudioLeft(newStartTime);

        // Magnetic snap to nearest segment edge
        double preSnapStart = newStartTime;
        newStartTime = _vm.SnapToSegmentEdge(newStartTime, segment.TrackId, segment.Id, _dragOp.SnapThreshold);
        double? snapTime = Math.Abs(newStartTime - preSnapStart) > 0.001 ? newStartTime : null;

        bool updated = _vm.UpdateSegmentTiming(segment, newStartTime, segment.EndTime);

        // Audio segments: update SourceStartOffset to match the left-trim delta
        if (updated && isAudio)
            _dragOp.UpdateAudioSourceOffset();

        return new DragResult { Segment = segment, Updated = updated, SnapIndicatorTime = updated ? snapTime : null };
    }
}

/// <summary>
/// Undo action that reverses a segment's track reassignment during a cross-track drag.
/// Moves the segment back to its original track on Undo and to the new track on Redo.
/// </summary>
internal sealed class SegmentTrackChangedAction : IUndoableAction
{
    private readonly Segment _segment;
    private readonly string _oldTrackId;
    private readonly string _newTrackId;
    private readonly TimelineViewModel _vm;

    public SegmentTrackChangedAction(Segment segment, string oldTrackId, string newTrackId, TimelineViewModel vm)
    {
        _segment = segment;
        _oldTrackId = oldTrackId;
        _newTrackId = newTrackId;
        _vm = vm;
    }

    public string Description => "Move segment to track";

    public void Undo()
    {
        MoveToTrack(_oldTrackId);
    }

    public void Redo()
    {
        MoveToTrack(_newTrackId);
    }

    private void MoveToTrack(string targetTrackId)
    {
        var sourceTrack = _vm.Tracks.FirstOrDefault(t => t.Id == _segment.TrackId);
        var targetTrack = _vm.Tracks.FirstOrDefault(t => t.Id == targetTrackId);
        if (sourceTrack == null || targetTrack == null) return;

        if (sourceTrack.Segments is ObservableCollection<Segment> sourceSegs)
            sourceSegs.Remove(_segment);

        _segment.TrackId = targetTrackId;

        if (targetTrack.Segments is ObservableCollection<Segment> targetSegs)
            targetSegs.Add(_segment);

        _vm.InvalidateActiveSegmentsCachePublic();
    }
}

/// <summary>
/// Undo action that reverses a ripple shift (Shift+drag) on a track's subsequent segments.
/// On Undo, shifts all segments after the dragged segment by −totalDelta.
/// On Redo, shifts them by +totalDelta.
/// </summary>
internal sealed class RippleShiftUndoAction : IUndoableAction
{
    private readonly TimelineViewModel _vm;
    private readonly string _trackId;
    private readonly string _excludeSegmentId;
    private readonly double _totalDelta;

    public RippleShiftUndoAction(TimelineViewModel vm, string trackId, string excludeSegmentId, double totalDelta)
    {
        _vm = vm;
        _trackId = trackId;
        _excludeSegmentId = excludeSegmentId;
        _totalDelta = totalDelta;
    }

    public string Description => "Ripple shift";

    public void Undo() => ApplyShift(-_totalDelta);
    public void Redo() => ApplyShift(_totalDelta);

    private void ApplyShift(double delta)
    {
        var segments = _vm.GetSegmentsForTrack(_trackId);
        // Find the dragged segment's current position to determine "after" threshold
        var dragged = segments.FirstOrDefault(s => s.Id == _excludeSegmentId);
        double threshold = dragged != null ? dragged.StartTime : 0;

        foreach (var seg in segments)
        {
            if (seg.Id == _excludeSegmentId) continue;
            if (seg.StartTime >= threshold - 0.001)
            {
                seg.StartTime = Math.Max(0, seg.StartTime + delta);
                seg.EndTime += delta;
            }
        }
        _vm.InvalidateActiveSegmentsCachePublic();
    }
}
