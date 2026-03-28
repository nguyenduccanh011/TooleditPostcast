using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Helpers;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Result of processing a drag delta frame. Contains all information the View
/// needs to update visuals — no ViewModel calls needed from the View.
/// </summary>
internal sealed class DragResult
{
    public required Segment Segment { get; init; }
    public bool Updated { get; init; }
    public double? SnapIndicatorTime { get; init; }
}

/// <summary>
/// Encapsulates all drag interaction logic (move, resize-left, resize-right)
/// so the View code-behind only forwards raw events and applies visual results.
/// Enables unit testing of the full drag pipeline without a WPF dependency.
/// </summary>
internal interface ISegmentDragHandler
{
    /// <summary>The active drag operation, or null when idle.</summary>
    SegmentDragOperation? ActiveDrag { get; }

    /// <summary>Begin a move drag on the given segment.</summary>
    void BeginMove(Segment segment, double frozenPPS);

    /// <summary>Begin a resize-right drag on the given segment.</summary>
    void BeginResizeRight(Segment segment, double frozenPPS);

    /// <summary>Begin a resize-left drag on the given segment.</summary>
    void BeginResizeLeft(Segment segment, double frozenPPS);

    /// <summary>
    /// Process a single drag delta frame. Returns a result indicating whether
    /// the segment was updated and any snap indicator position.
    /// When <paramref name="ripple"/> is true and the drag is a Move, all subsequent
    /// segments on the same track are shifted by the same delta (Shift+drag ripple mode).
    /// </summary>
    DragResult ProcessDelta(double horizontalChange, bool ripple = false);

    /// <summary>
    /// Complete the current drag and return an undo action (null if no change).
    /// May return a compound action if the segment was also moved to a different track.
    /// </summary>
    IUndoableAction? CompleteDrag();

    /// <summary>Cancel the current drag without recording undo.</summary>
    void CancelDrag();

    /// <summary>
    /// Attempt to move the currently dragged segment to a different track.
    /// Returns true if the segment was successfully moved.
    /// Only valid during a Move drag.
    /// </summary>
    bool TryMoveToTrack(Track targetTrack);

    /// <summary>
    /// Accumulate a vertical drag delta and return <see langword="true"/> when the
    /// total has exceeded the cross-track threshold.  Callers should only call
    /// <see cref="TryMoveToTrack"/> when this returns <see langword="true"/>.
    /// </summary>
    bool AccumulateVerticalDelta(double verticalChange);
}
