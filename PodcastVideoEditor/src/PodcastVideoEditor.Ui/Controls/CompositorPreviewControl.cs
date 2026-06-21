#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Single-pass Skia compositor preview. Builds a <see cref="CompositorScene"/> for the current
/// playhead and renders every visible layer in one pass — no per-frame FFmpeg, no PNG baking.
///
/// This is the foundation of the GPU compositor: the renderer is GPU-agnostic, so swapping the
/// presentation surface from <see cref="SKElement"/> (raster) to a GL surface (GLWpfControl /
/// D3DImage) later turns this into a fully GPU pipeline without touching the compositing logic.
/// During playback it repaints on the WPF render tick (~display refresh) for smooth scrubbing.
/// </summary>
public sealed class CompositorPreviewControl : SKElement, ICompositorPreviewSurface
{
    private readonly SkiaImageTextureCache _textures;
    private readonly SkiaSceneRenderer _renderer;
    private bool _renderLoopHooked;
    private ICompositorVisualizerSource? _visualizers;

    public ICompositorVisualizerSource? Visualizers
    {
        get => _visualizers;
        set { _visualizers = value; _renderer.Visualizers = value; }
    }

    public CompositorPreviewControl()
    {
        _textures = new SkiaImageTextureCache(onFrameReady: () =>
            Dispatcher.InvokeAsync(InvalidateVisual));
        _renderer = new SkiaSceneRenderer(_textures);

        Unloaded += (_, _) =>
        {
            UnhookRenderLoop();
            _textures.Dispose();
        };
    }

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<RenderVisualSegment>), typeof(CompositorPreviewControl),
        new PropertyMetadata(null, OnVisualChanged));

    public IReadOnlyList<RenderVisualSegment>? Segments
    {
        get => (IReadOnlyList<RenderVisualSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(CompositorPreviewControl),
        new PropertyMetadata(0.0, OnVisualChanged));

    public double PlayheadSeconds
    {
        get => (double)GetValue(PlayheadSecondsProperty);
        set => SetValue(PlayheadSecondsProperty, value);
    }

    public static readonly DependencyProperty OutputWidthProperty = DependencyProperty.Register(
        nameof(OutputWidth), typeof(int), typeof(CompositorPreviewControl),
        new PropertyMetadata(1080, OnVisualChanged));

    public int OutputWidth
    {
        get => (int)GetValue(OutputWidthProperty);
        set => SetValue(OutputWidthProperty, value);
    }

    public static readonly DependencyProperty OutputHeightProperty = DependencyProperty.Register(
        nameof(OutputHeight), typeof(int), typeof(CompositorPreviewControl),
        new PropertyMetadata(1920, OnVisualChanged));

    public int OutputHeight
    {
        get => (int)GetValue(OutputHeightProperty);
        set => SetValue(OutputHeightProperty, value);
    }

    public static readonly DependencyProperty FrameRateProperty = DependencyProperty.Register(
        nameof(FrameRate), typeof(int), typeof(CompositorPreviewControl),
        new PropertyMetadata(30));

    public int FrameRate
    {
        get => (int)GetValue(FrameRateProperty);
        set => SetValue(FrameRateProperty, value);
    }

    /// <summary>When true, repaints on every WPF render tick for smooth playback scrubbing.</summary>
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(CompositorPreviewControl),
        new PropertyMetadata(false, OnIsPlayingChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CompositorPreviewControl c)
            c.InvalidateVisual();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CompositorPreviewControl c)
            return;
        if ((bool)e.NewValue) c.HookRenderLoop();
        else c.UnhookRenderLoop();
    }

    private void HookRenderLoop()
    {
        if (_renderLoopHooked) return;
        CompositionTarget.Rendering += OnRenderTick;
        _renderLoopHooked = true;
    }

    private void UnhookRenderLoop()
    {
        if (!_renderLoopHooked) return;
        CompositionTarget.Rendering -= OnRenderTick;
        _renderLoopHooked = false;
    }

    private void OnRenderTick(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        CompositorPresent.Paint(
            e.Surface.Canvas, e.Info.Width, e.Info.Height,
            _renderer, Segments, PlayheadSeconds, OutputWidth, OutputHeight, FrameRate);
    }
}
