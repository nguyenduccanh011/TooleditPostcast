using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Visualizers;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for real-time spectrum visualization using SkiaSharp.
    /// Orchestrates FFT processing, smoothing, demo mode, and delegates rendering to IVisualizerRenderer.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private readonly IAudioTimelinePreviewService _audioService;
        private readonly VisualizerRendererRegistry _registry;
        private VisualizerConfig _config;
        
        // Shared spectrum processing (replaces duplicated FFT/smoothing/peak logic)
        private SpectrumProcessor _specProcessor;
        
        // Rendering state
        private double _colorTick;
        private bool _idleMode;
        private const float RenderScale = 0.5f;
        
        // Rendering control
        private volatile bool _isRunning;
        private CancellationTokenSource? _renderCts;
        private System.Diagnostics.Stopwatch _frameWatch = new();
        private int _targetFps = 30;
        private bool _disposed;
        
        // Double-buffering
        private SKBitmap? _frontBitmap;
        private SKBitmap? _backBitmap;
        private readonly object _bitmapLock = new object();

        // Events
        public event EventHandler<VisualizerFrameEventArgs>? FrameRendered;

        // Color tick speed constants
        private const double ColorTickIdle = 0.03;
        private const double ColorTickActive = 0.15;

        public VisualizerService(IAudioTimelinePreviewService audioService, VisualizerConfig? config = null)
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _registry = new VisualizerRendererRegistry();
            _config = config?.Clone() ?? new VisualizerConfig();
            _specProcessor = new SpectrumProcessor(_config.BandCount);

            Log.Information("VisualizerService initialized with {BandCount} bands, style: {Style}",
                _config.BandCount, _config.Style);
        }

        /// <summary>
        /// Update visualizer configuration.
        /// </summary>
        public void SetConfig(VisualizerConfig config)
        {
            if (!config.Validate())
                throw new ArgumentException("Invalid visualizer configuration", nameof(config));

            _config = config.Clone();
            _specProcessor.Reinitialize(_config.BandCount);
            Log.Information("VisualizerService config updated");
        }

        /// <summary>
        /// Start the rendering loop (background task at 60fps).
        /// </summary>
        public void Start(int width, int height)
        {
            if (_isRunning)
                return;
            if (width <= 0 || height <= 0)
            {
                Log.Warning("Visualizer Start() called with invalid dimensions {Width}x{Height}", width, height);
                return;
            }

            _isRunning = true;
            _renderCts = new CancellationTokenSource();
            EnsureBackBitmap(width, height);
            _frameWatch.Restart();

            // Start background rendering task
            _ = Task.Run(() => RenderingLoop(width, height, _renderCts.Token), _renderCts.Token);
            Log.Information("Visualizer started ({Width}x{Height})", width, height);
        }

        /// <summary>
        /// Stop the rendering loop.
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _renderCts?.Cancel();
            Log.Information("Visualizer stopped");
        }

        /// <summary>
        /// Main rendering loop — always renders (demo mode when audio not playing).
        /// </summary>
        private async Task RenderingLoop(int width, int height, CancellationToken cancellationToken)
        {
            try
            {
                var frameTimeMs = 1000 / Math.Max(1, _targetFps);

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    var frameStart = Environment.TickCount64;

                    // Always update spectrum: use real FFT when playing, idle animation otherwise
                    _idleMode = !_audioService.IsPlaying;
                    if (_audioService.IsPlaying)
                    {
                        UpdateSpectrumData();
                    }
                    else
                    {
                        _specProcessor.GenerateDemoSpectrum(_config, Environment.TickCount64 / 1000.0);
                    }

                    // Always render frame (no black frame ever)
                    RenderFrame(width, height);
                    // Idle: slow color drift so element looks "at rest"; active: normal rotation
                    _colorTick += _idleMode ? ColorTickIdle : ColorTickActive;

                    var frameTime = Environment.TickCount64 - frameStart;
                    var sleepTime = Math.Max(0, frameTimeMs - (int)frameTime);
                    if (sleepTime > 0)
                        await Task.Delay(sleepTime, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when Stop() is called
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in visualizer rendering loop");
            }
        }

        /// <summary>
        /// Update spectrum data with smoothing and peak hold.
        /// Falls back to demo data if FFT returns empty.
        /// </summary>
        private void UpdateSpectrumData()
        {
            var rawFFT = _audioService.GetFFTData(_config.BandCount * 8);

            if (rawFFT == null || rawFFT.Length == 0)
            {
                _specProcessor.GenerateDemoSpectrum(_config, Environment.TickCount64 / 1000.0);
                return;
            }

            _specProcessor.ProcessSpectrum(rawFFT, _config);

            // If ProcessSpectrum decayed to silence (all near-zero magnitudes),
            // fall back to demo animation so the visualizer is never blank.
            if (_specProcessor.IsSilent(0.001f))
                _specProcessor.GenerateDemoSpectrum(_config, Environment.TickCount64 / 1000.0);
        }

        /// <summary>
        /// Render a single frame using the registered renderer for the current style.
        /// Uses double-buffering to avoid tearing.
        /// </summary>
        private void RenderFrame(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            var scaledWidth = Math.Max(64, (int)(width * RenderScale));
            var scaledHeight = Math.Max(64, (int)(height * RenderScale));

            EnsureBackBitmap(scaledWidth, scaledHeight);
            if (_backBitmap == null) return;

            _frameWatch.Restart();

            // Build spectrum/peaks arrays for rendering (mirror transform or raw)
            float[] spectrumToRender;
            float[] peaksToRender;
            if (_config.SymmetricMode)
                spectrumToRender = _specProcessor.BuildMirroredSpectrum(out peaksToRender);
            else
            {
                spectrumToRender = _specProcessor.CurrentSpectrum;
                peaksToRender = _specProcessor.PeakBars;
            }

            using (var canvas = new SKCanvas(_backBitmap))
            {
                canvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);

                var renderer = _registry.GetRenderer(_config.Style);
                renderer.Render(canvas, spectrumToRender, peaksToRender, _config,
                               scaledWidth, scaledHeight, _colorTick);
            }

            // Swap front and back buffers atomically
            lock (_bitmapLock)
            {
                (_frontBitmap, _backBitmap) = (_backBitmap, _frontBitmap);
            }

            FrameRendered?.Invoke(this, new VisualizerFrameEventArgs { Bitmap = _frontBitmap! });

            _frameWatch.Stop();
            if (_frameWatch.ElapsedMilliseconds > 30 && _targetFps > 20)
                _targetFps = 20;
            else if (_frameWatch.ElapsedMilliseconds < 18 && _targetFps < 30)
                _targetFps = 30;
        }

        /// <summary>
        /// Get the current rendered bitmap (front buffer — safe to read while back buffer is being rendered).
        /// </summary>
        public SKBitmap? GetCurrentBitmap()
        {
            lock (_bitmapLock)
            {
                return _frontBitmap;
            }
        }

        /// <summary>
        /// Get the renderer registry (for UI to enumerate available styles).
        /// </summary>
        public VisualizerRendererRegistry Registry => _registry;

        private void EnsureBackBitmap(int width, int height)
        {
            if (_backBitmap == null || _backBitmap.Width != width || _backBitmap.Height != height)
            {
                _backBitmap?.Dispose();
                _backBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            }
            // Also ensure front bitmap matches
            lock (_bitmapLock)
            {
                if (_frontBitmap == null || _frontBitmap.Width != width || _frontBitmap.Height != height)
                {
                    _frontBitmap?.Dispose();
                    _frontBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                }
            }
        }

        /// <summary>
        /// Get current visualizer state.
        /// </summary>
        public bool IsRunning => _isRunning;
        public VisualizerConfig Config => _config.Clone();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();
            _renderCts?.Dispose();
            lock (_bitmapLock)
            {
                _frontBitmap?.Dispose();
                _frontBitmap = null;
            }
            _backBitmap?.Dispose();
            _backBitmap = null;
            _registry.Dispose();
            Log.Information("VisualizerService disposed");
        }
    }

    /// <summary>
    /// Event args for visualizer frame rendering.
    /// </summary>
    public class VisualizerFrameEventArgs : EventArgs
    {
        public SKBitmap Bitmap { get; set; } = null!;
    }
}
