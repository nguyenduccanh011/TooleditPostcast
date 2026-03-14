#nullable enable
using System;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Mediates bidirectional selection sync between Timeline and Canvas.
/// When a segment is selected on the timeline, linked canvas elements are highlighted.
/// When an element is selected on the canvas, its linked segment is highlighted on the timeline.
/// Prevents circular event loops via a guard flag.
/// </summary>
public class SelectionSyncService
{
    private bool _isSyncing;

    /// <summary>
    /// Raised when timeline selection changes and canvas should update.
    /// Parameters: segmentId (null = deselected), playheadInRange (whether playhead is within segment bounds).
    /// </summary>
    public event Action<string?, bool>? TimelineSelectionChanged;

    /// <summary>
    /// Raised when canvas element selection changes and timeline should update.
    /// Parameters: segmentId of the selected element (null = global element or deselected).
    /// </summary>
    public event Action<string?>? CanvasSelectionChanged;

    /// <summary>
    /// Notify that a segment was selected on the timeline.
    /// </summary>
    public void NotifyTimelineSegmentSelected(string? segmentId, bool playheadInRange)
    {
        if (_isSyncing)
            return;

        _isSyncing = true;
        try
        {
            TimelineSelectionChanged?.Invoke(segmentId, playheadInRange);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    /// <summary>
    /// Notify that a canvas element was selected.
    /// </summary>
    public void NotifyCanvasElementSelected(string? segmentId)
    {
        if (_isSyncing)
            return;

        _isSyncing = true;
        try
        {
            CanvasSelectionChanged?.Invoke(segmentId);
        }
        finally
        {
            _isSyncing = false;
        }
    }
}
