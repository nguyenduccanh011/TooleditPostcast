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
        
        // FFT and smoothing data
        private float[] _currentSpectrum = Array.Empty<float>();
        private float[] _previousSpectrum = Array.Empty<float>();
        private float[] _peakBars = Array.Empty<float>();
        private long[] _peakHoldTimes = Array.Empty<long>();
        
        // Rendering state
        private double _colorTick;
        private const float RenderScale = 0.5f;
        
        // Rendering control
        private bool _isRunning;
        private CancellationTokenSource? _renderCts;
        private System.Diagnostics.Stopwatch _frameWatch = new();
        private int _targetFps = 30;
        
        // Double-buffering
        private SKBitmap? _frontBitmap;
        private SKBitmap? _backBitmap;
        private readonly object _bitmapLock = new object();

        // Events
        public event EventHandler<VisualizerFrameEventArgs>? FrameRendered;

        public VisualizerService(IAudioTimelinePreviewService audioService, VisualizerConfig? config = null)
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _registry = new VisualizerRendererRegistry();
            _config = config?.Clone() ?? new VisualizerConfig();

            InitializeBuffers();
            Log.Information("VisualizerService initialized with {BandCount} bands, style: {Style}",
                _config.BandCount, _config.Style);
        }

        /// <summary>
        /// Initialize FFT and peak-hold buffers.
        /// </summary>
        private void InitializeBuffers()
        {
            _currentSpectrum = new float[_config.BandCount];
            _previousSpectrum = new float[_config.BandCount];
            _peakBars = new float[_config.BandCount];
            _peakHoldTimes = new long[_config.BandCount];

            Array.Clear(_currentSpectrum, 0, _currentSpectrum.Length);
            Array.Clear(_previousSpectrum, 0, _previousSpectrum.Length);
            Array.Clear(_peakBars, 0, _peakBars.Length);
        }

        /// <summary>
        /// Linear interpolation helper function.
        /// </summary>
        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Clamp(t, 0f, 1f);
        }

        /// <summary>
        /// Update visualizer configuration.
        /// </summary>
        public void SetConfig(VisualizerConfig config)
        {
            if (!config.Validate())
                throw new ArgumentException("Invalid visualizer configuration", nameof(config));

            _config = config.Clone();
            InitializeBuffers();
            Log.Information("VisualizerService config updated");
        }

        /// <summary>
        /// Start the rendering loop (background task at 60fps).
        /// </summary>
        public void Start(int width, int height)
        {
            if (_isRunning)
                return;

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

                    // Always update spectrum: use real FFT when playing, demo data otherwise
                    if (_audioService.IsPlaying)
                    {
                        UpdateSpectrumData();
                    }
                    else
                    {
                        GenerateDemoSpectrum();
                    }

                    // Always render frame (no black frame ever)
                    RenderFrame(width, height);
                    _colorTick += 0.15; // Slow color rotation so bars don't appear to scroll laterally

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
                // Fallback: generate demo data instead of showing black
                GenerateDemoSpectrum();
                return;
            }

            var maxIndex = Math.Max(1, (int)(rawFFT.Length * 0.6f));
            var maxValue = 0f;
            for (int i = 0; i < maxIndex; i++)
            {
                if (rawFFT[i] > maxValue)
                    maxValue = rawFFT[i];
            }

            if (maxValue <= 1e-6f)
            {
                GenerateDemoSpectrum();
                return;
            }

            // Downsample to band count with auto-gain
            for (int i = 0; i < _config.BandCount; i++)
            {
                var start = (int)(i * (maxIndex / (float)_config.BandCount));
                var end = (int)((i + 1) * (maxIndex / (float)_config.BandCount));
                if (end <= start)
                    end = Math.Min(maxIndex, start + 1);

                float sum = 0f;
                for (int j = start; j < end; j++)
                    sum += rawFFT[j];

                float avg = sum / (end - start);
                float newValue = avg / maxValue; // auto-gain normalize
                newValue = MathF.Pow(Math.Clamp(newValue, 0f, 1f), 0.7f); // boost low levels

                // Apply smoothing with exponential decay
                _currentSpectrum[i] = Lerp(_previousSpectrum[i], newValue, 1f - _config.SmoothingFactor);

                // Handle peak hold
                if (_currentSpectrum[i] > _peakBars[i])
                {
                    _peakBars[i] = _currentSpectrum[i];
                    _peakHoldTimes[i] = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - _peakHoldTimes[i] > _config.PeakHoldTime)
                {
                    // Peak hold time expired, start decay
                    _peakBars[i] = Lerp(_peakBars[i], 0f, 0.05f);
                }
            }

            // Store current as previous for next frame
            Array.Copy(_currentSpectrum, _previousSpectrum, _config.BandCount);
        }

        /// <summary>
        /// Generate animated demo spectrum data when audio is not playing.
        /// Produces a smooth, visually appealing preview pattern.
        /// </summary>
        private void GenerateDemoSpectrum()
        {
            var time = Environment.TickCount64 / 1000.0;
            for (int i = 0; i < _config.BandCount; i++)
            {
                var freq = i / (float)_config.BandCount;
                // Standing waves: each band oscillates in-place at its own unique frequency.
                // No (time * speed + freq * phase) pattern — avoids left-to-right traveling motion.
                var bandFreq = 1.0 + freq * 4.0;   // each band has its own oscillation speed
                var val = 0.25f
                             + 0.20f * MathF.Sin((float)(time * bandFreq))
                             + 0.10f * MathF.Sin((float)(time * bandFreq * 1.7))
                             + 0.08f * MathF.Sin((float)(time * bandFreq * 0.5));
                // Lower frequencies tend to be louder (more natural)
                val *= 1.0f - freq * 0.4f;
                var newValue = Math.Clamp(val, 0f, 1f);

                _currentSpectrum[i] = Lerp(_previousSpectrum[i], newValue, 1f - _config.SmoothingFactor);

                if (_currentSpectrum[i] > _peakBars[i])
                {
                    _peakBars[i] = _currentSpectrum[i];
                    _peakHoldTimes[i] = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - _peakHoldTimes[i] > _config.PeakHoldTime)
                {
                    _peakBars[i] = Lerp(_peakBars[i], 0f, 0.05f);
                }
            }
            Array.Copy(_currentSpectrum, _previousSpectrum, _config.BandCount);
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

            using (var canvas = new SKCanvas(_backBitmap))
            {
                canvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);

                var renderer = _registry.GetRenderer(_config.Style);
                renderer.Render(canvas, _currentSpectrum, _peakBars, _config,
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
