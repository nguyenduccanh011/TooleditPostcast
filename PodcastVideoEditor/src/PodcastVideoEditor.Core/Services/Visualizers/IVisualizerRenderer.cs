using PodcastVideoEditor.Core.Models;
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Strategy interface for spectrum visualization rendering.
/// Each visualization style implements this interface as a separate class.
/// Adding a new style = implementing this interface + registering in VisualizerRendererRegistry.
/// </summary>
public interface IVisualizerRenderer : IDisposable
{
    /// <summary>Display name for UI.</summary>
    string Name { get; }

    /// <summary>Short description for tooltips / preview.</summary>
    string Description { get; }

    /// <summary>The VisualizerStyle enum value this renderer handles.</summary>
    VisualizerStyle Style { get; }

    /// <summary>
    /// Render a single frame onto the provided SKCanvas.
    /// Called from background rendering loop at ~30fps.
    /// </summary>
    /// <param name="canvas">SkiaSharp canvas to draw on (already cleared to black).</param>
    /// <param name="spectrum">Normalized spectrum data [0..1] per band.</param>
    /// <param name="peakBars">Peak hold values [0..1] per band.</param>
    /// <param name="config">Current visualizer configuration.</param>
    /// <param name="width">Canvas width in pixels (already scaled).</param>
    /// <param name="height">Canvas height in pixels (already scaled).</param>
    /// <param name="colorTick">Advancing hue tick for color animation.</param>
    void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                VisualizerConfig config, int width, int height, double colorTick);
}
