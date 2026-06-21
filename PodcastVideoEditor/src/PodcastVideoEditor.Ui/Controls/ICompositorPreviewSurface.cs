#nullable enable
using System.Collections.Generic;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Common surface contract implemented by both the raster (<see cref="CompositorPreviewControl"/>)
/// and GPU (<see cref="GpuCompositorPreviewControl"/>) compositor preview controls, so a host can
/// drive either one identically and fall back transparently when GPU init fails.
/// </summary>
public interface ICompositorPreviewSurface
{
    IReadOnlyList<RenderVisualSegment>? Segments { get; set; }
    double PlayheadSeconds { get; set; }
    int OutputWidth { get; set; }
    int OutputHeight { get; set; }
    int FrameRate { get; set; }
    bool IsPlaying { get; set; }
    ICompositorVisualizerSource? Visualizers { get; set; }
}
