#nullable enable
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// Draws a procedural audio-spectrum visualizer directly onto the compositor canvas at a given
/// time. Invoked by <see cref="SkiaSceneRenderer"/> for visualizer layers. The canvas is already
/// translated so (0,0) is the layer's top-left; draw within [0,width]×[0,height].
/// </summary>
public interface IVisualizerLayerPainter
{
    void Paint(SKCanvas canvas, int width, int height, double timeSeconds);
}

/// <summary>
/// Supplies the visualizer draws (destination rect + painter) active at a given time. Implemented
/// by the UI, which owns the audio readers and per-element painters.
/// </summary>
public interface ICompositorVisualizerSource
{
    /// <summary>Returns the visualizer layers to draw (in back-to-front order) at the given time.</summary>
    System.Collections.Generic.IReadOnlyList<VisualizerDraw> GetActiveDraws(double timeSeconds);
}

/// <summary>A single visualizer to draw: where (output pixels) and how.</summary>
public readonly struct VisualizerDraw
{
    public VisualizerDraw(SKRect dest, IVisualizerLayerPainter painter)
    {
        Dest = dest;
        Painter = painter;
    }

    public SKRect Dest { get; }
    public IVisualizerLayerPainter Painter { get; }
}
