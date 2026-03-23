#nullable enable
using PodcastVideoEditor.Core.Models;
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Immutable snapshot of all data needed for a render pass.
/// Created once before render starts, isolating the pipeline from
/// live UI state (CanvasViewModel, TimelineViewModel).
///
/// Commercial pattern: equivalent to Premiere's "render context" or
/// DaVinci Resolve's "timeline snapshot" — the render pipeline never
/// reads from mutable UI objects after this snapshot is created.
/// </summary>
public sealed class RenderSnapshot
{
    /// <summary>
    /// Deep-cloned project with live timeline tracks merged in.
    /// </summary>
    public required Project Project { get; init; }

    /// <summary>
    /// Deep-cloned canvas elements with IsVisible forced true.
    /// Null when no elements exist.
    /// </summary>
    public required IReadOnlyList<CanvasElement>? Elements { get; init; }

    /// <summary>
    /// Canvas dimensions at snapshot time (for coordinate mapping).
    /// </summary>
    public required double CanvasWidth { get; init; }
    public required double CanvasHeight { get; init; }

    /// <summary>
    /// Pre-built element-segment registry for O(1) lookups.
    /// </summary>
    public required ElementSegmentRegistry Registry { get; init; }
}
