#nullable enable
using System;
using System.Collections.Generic;
using PodcastVideoEditor.Core.Services.Compositing;
using PodcastVideoEditor.Ui.Controls;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Texture source for compositor EXPORT: still images via the bounded image cache (same as
/// preview), and embedded VIDEO clips via frame-accurate <see cref="VideoStreamDecoder"/>s
/// (one per video layer, opened seeked to the layer's first requested source time and read
/// forward). Image-only projects never create a decoder, so they behave exactly like the preview.
/// One instance per export chunk (single-threaded use).
/// </summary>
public sealed class CompositorExportTextureSource : ICompositorTextureSource, IDisposable
{
    private readonly SkiaImageTextureCache _images = new();
    private readonly Dictionary<string, VideoStreamDecoder> _videos = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ffmpegPath;
    private readonly int _fps;
    private bool _disposed;

    public CompositorExportTextureSource(string ffmpegPath, int fps)
    {
        _ffmpegPath = ffmpegPath;
        _fps = Math.Max(1, fps);
    }

    public SKImage? GetImage(CompositorLayer layer, double timeSeconds)
    {
        if (_disposed || layer == null)
            return null;

        if (layer.Kind != CompositorLayerKind.Video)
            return _images.GetImage(layer, timeSeconds);

        // Map timeline time -> source time and pull the frame from a forward-only decoder.
        var sourceTime = Math.Max(0, (timeSeconds - layer.ClipStartTime) + layer.SourceOffsetSeconds);
        var dw = Math.Max(2, (int)Math.Round(layer.DestWidth));
        var dh = Math.Max(2, (int)Math.Round(layer.DestHeight));

        // Key per CLIP (source + clip start), not just source path: the same video reused in two
        // clips needs an independent forward-only decoder, else the second clip reads stale frames.
        var key = $"{layer.SourceKey}|{layer.ClipStartTime:F3}";
        if (!_videos.TryGetValue(key, out var decoder))
        {
            decoder = new VideoStreamDecoder(_ffmpegPath, layer.SourceKey, sourceTime, dw, dh, _fps);
            _videos[key] = decoder;
        }
        return decoder.GetFrameAtSource(sourceTime);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _images.Dispose();
        foreach (var d in _videos.Values)
            d.Dispose();
        _videos.Clear();
    }
}
