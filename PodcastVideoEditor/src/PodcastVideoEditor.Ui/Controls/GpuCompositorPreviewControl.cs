#nullable enable
using System;
using System.Collections.Generic;
using System.Windows.Media;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// GPU-accelerated compositor preview. Hosts an OpenGL surface (GLWpfControl, ANGLE/D3D-backed on
/// Windows), wraps its framebuffer as a SkiaSharp GPU surface (GRContext) and runs the identical
/// <see cref="SkiaSceneRenderer"/> as the raster control — so compositing (scale, Ken-Burns, fade,
/// tint, overlay) happens on the GPU each frame, the way CapCut does it.
///
/// Falls back gracefully: if GL initialisation throws on a given machine, <see cref="GlReady"/>
/// stays false and the host can swap in the raster control.
/// </summary>
public sealed class GpuCompositorPreviewControl : GLWpfControl, ICompositorPreviewSurface
{
    private const uint GL_RGBA8 = 0x8058;

    private readonly SkiaImageTextureCache _textures;
    private readonly SkiaSceneRenderer _renderer;
    private GRContext? _grContext;
    private bool _started;
    private bool _firstTickLogged;
    private bool _failedFired;

    /// <summary>True once GL has started successfully and the first frame rendered without error.</summary>
    public bool GlReady { get; private set; }

    /// <summary>Raised on the UI thread if GL initialisation/first render fails (host should fall back).</summary>
    public event EventHandler? GlFailed;

    public GpuCompositorPreviewControl()
    {
        _textures = new SkiaImageTextureCache(onFrameReady: () => Dispatcher.InvokeAsync(InvalidateVisual));
        _renderer = new SkiaSceneRenderer(_textures);

        Loaded += (_, _) =>
        {
            EnsureStarted();
            // Drive continuous repaint off the WPF render tick — GLWpfControl's RenderContinuously
            // alone did not re-raise Render here, so the frame froze at t=0 and playhead/scrub
            // updates never re-rendered. InvalidateVisual each tick forces a fresh GL frame.
            CompositionTarget.Rendering += OnCompositionTick;
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnCompositionTick;
            try { _grContext?.Dispose(); } catch { /* ignore */ }
            _grContext = null;
            _textures.Dispose();
        };
        Render += OnGlRender;
    }

    private void OnCompositionTick(object? sender, EventArgs e) => InvalidateVisual();

    public IReadOnlyList<RenderVisualSegment>? Segments { get; set; }
    public double PlayheadSeconds { get; set; }
    public int OutputWidth { get; set; } = 1080;
    public int OutputHeight { get; set; } = 1920;
    public int FrameRate { get; set; } = 30;

    // Rendering is continuous while loaded, so playback needs no extra gating; kept for the interface.
    public bool IsPlaying { get; set; }

    private ICompositorVisualizerSource? _visualizers;
    public ICompositorVisualizerSource? Visualizers
    {
        get => _visualizers;
        set { _visualizers = value; _renderer.Visualizers = value; }
    }

    private void EnsureStarted()
    {
        if (_started)
            return;
        try
        {
            // Use default context settings (let GLWpfControl negotiate a compatible GL version);
            // explicitly requesting 3.3 core could create a context whose render loop never starts
            // on some drivers. RenderContinuously drives a frame every WPF render tick.
            Start(new GLWpfControlSettings { RenderContinuously = true });
            _started = true;
            Log.Information("GPU compositor: GLWpfControl.Start succeeded; kicking first render");
            // Nudge the render loop in case it needs an initial invalidation to begin.
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GPU compositor: GLWpfControl.Start failed — GPU preview unavailable");
            FireFailedOnce();
        }
    }

    private void FireFailedOnce()
    {
        if (_failedFired)
            return;
        _failedFired = true;
        GlFailed?.Invoke(this, EventArgs.Empty);
    }

    private void OnGlRender(TimeSpan delta)
    {
        try
        {
            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                Log.Information("GPU compositor: first GL render tick");
            }

            if (_grContext == null)
            {
                var iface = GRGlInterface.Create();
                if (iface == null)
                {
                    Log.Error("GPU compositor: GRGlInterface.Create() returned null");
                    FireFailedOnce();
                    return;
                }
                _grContext = GRContext.CreateGl(iface);
                if (_grContext == null)
                {
                    Log.Error("GPU compositor: GRContext.CreateGl() returned null");
                    FireFailedOnce();
                    return;
                }
            }

            // Query the bound framebuffer + viewport set up by GLWpfControl for this frame.
            var vp = new int[4];
            GL.GetInteger(GetPName.Viewport, vp);
            var width = vp[2] > 0 ? vp[2] : Math.Max(1, (int)ActualWidth);
            var height = vp[3] > 0 ? vp[3] : Math.Max(1, (int)ActualHeight);
            var fbo = GL.GetInteger(GetPName.FramebufferBinding);

            var glInfo = new GRGlFramebufferInfo((uint)fbo, GL_RGBA8);
            using var renderTarget = new GRBackendRenderTarget(width, height, sampleCount: 0, stencilBits: 8, glInfo);
            using var surface = SKSurface.Create(_grContext, renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
            if (surface == null)
            {
                Log.Error("GPU compositor: SKSurface.Create returned null (fbo={Fbo}, {W}x{H})", fbo, width, height);
                FireFailedOnce();
                return;
            }

            CompositorPresent.Paint(
                surface.Canvas, width, height,
                _renderer, Segments, PlayheadSeconds, OutputWidth, OutputHeight, FrameRate);

            surface.Canvas.Flush();
            _grContext.Flush();

            if (!GlReady)
            {
                GlReady = true;
                Log.Information("GPU compositor: GL surface ready ({Width}x{Height}, fbo={Fbo})", width, height, fbo);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GPU compositor render failed");
            if (!GlReady)
                GlFailed?.Invoke(this, EventArgs.Empty);
        }
    }
}
