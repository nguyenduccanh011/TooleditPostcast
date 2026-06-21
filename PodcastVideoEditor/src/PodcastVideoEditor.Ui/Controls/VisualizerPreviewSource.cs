#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Live visualizer source for the compositor preview. Holds one lazily-created
/// <see cref="VisualizerCompositorLayerPainter"/> per visualizer element (each owns an audio
/// reader) and returns the ones active at a given time. Disposed with the preview window.
/// </summary>
public sealed class VisualizerPreviewSource : ICompositorVisualizerSource, IDisposable
{
    /// <summary>One visualizer element: config, destination rect (output px) and active time range.</summary>
    public sealed class Descriptor
    {
        public required VisualizerConfig Config { get; init; }
        public SKRect Dest { get; init; }
        public double Start { get; init; }
        public double End { get; init; }
    }

    private readonly string? _audioPath;
    private readonly int _analysisFps;
    private readonly List<Descriptor> _descriptors;
    private readonly VisualizerCompositorLayerPainter?[] _painters;
    private readonly List<VisualizerDraw> _scratch = new();
    private bool _disposed;

    public VisualizerPreviewSource(string? audioPath, IReadOnlyList<Descriptor> descriptors, int analysisFps)
    {
        _audioPath = audioPath;
        _analysisFps = analysisFps;
        _descriptors = descriptors?.ToList() ?? new List<Descriptor>();
        _painters = new VisualizerCompositorLayerPainter?[_descriptors.Count];
    }

    public bool HasAny => !string.IsNullOrEmpty(_audioPath) && _descriptors.Count > 0;

    /// <summary>
    /// Creates an independent source sharing the same (immutable) descriptors/audio/fps but with
    /// its own painter set. Used by parallel export so each worker thread has isolated state.
    /// </summary>
    public VisualizerPreviewSource CreateSibling() => new(_audioPath, _descriptors, _analysisFps);

    /// <summary>Eagerly create all painters and block until their spectrum precompute completes.
    /// Used by export so every frame has the correct (not demo) spectrum.</summary>
    public void PrewarmAndWait(int timeoutMs)
    {
        if (_disposed || string.IsNullOrEmpty(_audioPath))
            return;
        for (int i = 0; i < _descriptors.Count; i++)
        {
            var d = _descriptors[i];
            _painters[i] ??= new VisualizerCompositorLayerPainter(_audioPath!, d.Config, d.Start, d.End, _analysisFps);
        }
        foreach (var p in _painters)
            p?.WaitUntilReady(timeoutMs);
    }

    public IReadOnlyList<VisualizerDraw> GetActiveDraws(double timeSeconds)
    {
        _scratch.Clear();
        if (_disposed || string.IsNullOrEmpty(_audioPath))
            return _scratch;

        for (int i = 0; i < _descriptors.Count; i++)
        {
            var d = _descriptors[i];
            if (timeSeconds < d.Start || timeSeconds >= d.End)
                continue;
            if (d.Dest.Width <= 0 || d.Dest.Height <= 0)
                continue;

            var painter = _painters[i] ??= new VisualizerCompositorLayerPainter(_audioPath!, d.Config, d.Start, d.End, _analysisFps);
            _scratch.Add(new VisualizerDraw(d.Dest, painter));
        }
        return _scratch;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _painters)
            p?.Dispose();
    }
}
