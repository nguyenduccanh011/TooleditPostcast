namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// Named constants for timeline layout, durations, and buffers.
/// Centralises magic numbers that were previously scattered across TimelineViewModel and TimelineView.
/// </summary>
internal static class TimelineConstants
{
    /// <summary>Default duration (seconds) for new segments when no asset duration is known.</summary>
    public const double DefaultSegmentDuration = 5.0;

    /// <summary>Gap (seconds) inserted before a duplicated segment.</summary>
    public const double DuplicateGapSeconds = 0.5;

    /// <summary>Buffer (seconds) added after the last segment when computing TotalDuration.</summary>
    public const double SegmentEndBuffer = 5.0;

    /// <summary>Buffer (seconds) added to TotalDuration for display (zoom calculations).</summary>
    public const double DisplayDurationBuffer = 10.0;

    /// <summary>Default TotalDuration (seconds) when the timeline has no segments.</summary>
    public const double DefaultEmptyDuration = 60.0;

    /// <summary>How far beyond content the playhead/scrub can seek (seconds).</summary>
    public const double PlayheadOvershoot = 30.0;

    /// <summary>Minimum timeline width in pixels.</summary>
    public const double MinTimelineWidth = 800.0;

    /// <summary>Minimum PixelsPerSecond to avoid extreme zoom-out.</summary>
    public const double MinPixelsPerSecond = 1.0;

    /// <summary>Magnetic snap threshold in pixels (converted to seconds via PPS at drag start).</summary>
    public const double SnapPixelThreshold = 15.0;

    /// <summary>Tolerance (seconds) for undo action change detection.</summary>
    public const double UndoTolerance = 0.001;
}
