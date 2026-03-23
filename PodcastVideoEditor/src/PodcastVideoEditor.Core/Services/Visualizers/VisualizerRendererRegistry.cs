using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Registry mapping VisualizerStyle → IVisualizerRenderer.
/// Adding a new style: 1) add enum value, 2) create renderer class, 3) Register() here.
/// </summary>
public sealed class VisualizerRendererRegistry : IDisposable
{
    private readonly Dictionary<VisualizerStyle, IVisualizerRenderer> _renderers = new();

    public VisualizerRendererRegistry()
    {
        // Register all built-in renderers
        Register(new BarsRenderer());
        Register(new WaveformRenderer());
        Register(new CircularRenderer());
        Register(new NeonGlowRenderer());
        Register(new ParticlesRenderer());
        Register(new RingRenderer());
        Register(new LineWaveRenderer());
    }

    public void Register(IVisualizerRenderer renderer)
    {
        _renderers[renderer.Style] = renderer;
    }

    public IVisualizerRenderer GetRenderer(VisualizerStyle style)
    {
        return _renderers.TryGetValue(style, out var renderer) ? renderer : _renderers[VisualizerStyle.Bars];
    }

    public IReadOnlyCollection<IVisualizerRenderer> GetAll() => _renderers.Values;

    public void Dispose()
    {
        foreach (var r in _renderers.Values)
            r.Dispose();
        _renderers.Clear();
    }
}
