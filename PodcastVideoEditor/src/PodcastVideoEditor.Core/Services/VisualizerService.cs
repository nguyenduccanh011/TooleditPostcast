using PodcastVideoEditor.Core.Models;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for real-time spectrum visualization using SkiaSharp.
    /// Processes FFT data and renders visualizations at 60fps.
    /// </summary>
    public class VisualizerService : IDisposable
    {
        private readonly AudioService _audioService;
        private VisualizerConfig _config;
        
        // FFT and smoothing data
        private float[] _currentSpectrum;
        private float[] _previousSpectrum;
        private float[] _peakBars;
        private long[] _peakHoldTimes;
        
        // Rendering state
        private double _colorTick;
        private const float RenderScale = 0.5f; // render at lower resolution then scale up for UI
        
        // Rendering control
        private bool _isRunning;
        private CancellationTokenSource? _renderCts;
        private System.Diagnostics.Stopwatch _frameWatch = new();
        private int _targetFps = 30;
        
        // Bitmap rendering
        private SKBitmap? _currentBitmap;
        private readonly object _bitmapLock = new object();
        private readonly SKPaint _barPaint = new() { IsAntialias = false };
        private readonly SKPaint _peakPaint = new() { IsAntialias = false, Color = SKColors.White };
        private readonly SKPaint _linePaint = new() { IsAntialias = false, Color = new SKColor(255, 255, 255, 40), StrokeWidth = 1f };
        private readonly SKPaint _wavePaint = new() { IsAntialias = false, StrokeWidth = 2f, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _circularPaint = new() { IsAntialias = false, StrokeWidth = 2f };
        private readonly SKPath _wavePath = new();

        // Events
        public event EventHandler<VisualizerFrameEventArgs>? FrameRendered;

        public VisualizerService(AudioService audioService, VisualizerConfig? config = null)
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
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
            EnsureBitmap(width, height);
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
        /// Main rendering loop (runs at ~60fps in background).
        /// </summary>
        private async Task RenderingLoop(int width, int height, CancellationToken cancellationToken)
        {
            try
            {
                var frameTimeMs = 1000 / Math.Max(1, _targetFps);

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    var frameStart = Environment.TickCount64;

                    // Skip rendering if audio not playing to reduce load
                    if (!_audioService.IsPlaying)
                    {
                        await Task.Delay(frameTimeMs, cancellationToken);
                        continue;
                    }

                    // Update spectrum data from audio
                    UpdateSpectrumData();

                    // Render frame
                    RenderFrame(width, height);
                    _colorTick += 0.8; // advance hue over time

                    // Maintain 60fps
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
        /// </summary>
        private void UpdateSpectrumData()
        {
            // Get raw FFT data from audio service
            var rawFFT = _audioService.GetFFTData(_config.BandCount * 8);

            if (rawFFT == null || rawFFT.Length == 0)
                return;

            // Focus on lower frequencies (more visible movement)
            var maxIndex = Math.Max(1, (int)(rawFFT.Length * 0.6f));
            var maxValue = 0f;
            for (int i = 0; i < maxIndex; i++)
            {
                if (rawFFT[i] > maxValue)
                    maxValue = rawFFT[i];
            }

            if (maxValue <= 1e-6f)
                return;

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
        /// Render a single frame to bitmap.
        /// </summary>
        private void RenderFrame(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            var scaledWidth = Math.Max(64, (int)(width * RenderScale));
            var scaledHeight = Math.Max(64, (int)(height * RenderScale));

            EnsureBitmap(scaledWidth, scaledHeight);
            SKBitmap? bitmap;
            lock (_bitmapLock)
            {
                bitmap = _currentBitmap;
            }

            if (bitmap == null)
                return;

            _frameWatch.Restart();

            using (var canvas = new SKCanvas(bitmap))
            {
                // Draw background
                canvas.DrawColor(SKColors.Black, SKBlendMode.Src);

                // Draw visualizer based on style
                switch (_config.Style)
                {
                    case VisualizerStyle.Bars:
                        DrawBars(canvas, scaledWidth, scaledHeight);
                        break;

                    case VisualizerStyle.Waveform:
                        DrawWaveform(canvas, scaledWidth, scaledHeight);
                        break;

                    case VisualizerStyle.Circular:
                        DrawCircular(canvas, scaledWidth, scaledHeight);
                        break;
                }

                // Fire event
                FrameRendered?.Invoke(this, new VisualizerFrameEventArgs { Bitmap = bitmap });
            }

            _frameWatch.Stop();
            // Adaptive FPS: keep in 20-30 range to protect UI responsiveness
            if (_frameWatch.ElapsedMilliseconds > 30 && _targetFps > 20)
            {
                _targetFps = 20;
            }
            else if (_frameWatch.ElapsedMilliseconds < 18 && _targetFps < 30)
            {
                _targetFps = 30;
            }
        }

        /// <summary>
        /// Draw bars visualization.
        /// </summary>
        private void DrawBars(SKCanvas canvas, int width, int height)
        {
            var centerY = height / 2f;
            var barWidth = (width - (_config.BandCount - 1) * _config.BarSpacing) / _config.BandCount;
            var maxBarHeight = height * 0.45f;

            // Horizon line
            canvas.DrawLine(0, centerY, width, centerY, _linePaint);

            for (int i = 0; i < _config.BandCount; i++)
            {
                if (i >= _currentSpectrum.Length)
                    break;

                var value = Math.Clamp(_currentSpectrum[i], 0f, 1f);
                var barHeight = value * maxBarHeight;
                var x = i * (barWidth + _config.BarSpacing);
                var color = GetNeonColor(i, _config.BandCount);

                _barPaint.Color = color;
                var rect = new SKRect(x, centerY - barHeight, x + barWidth, centerY + barHeight);
                canvas.DrawRect(rect, _barPaint);

                // Caps (peak hold)
                if (_config.ShowPeaks && _peakBars[i] > 0)
                {
                    var capHeight = 4f;
                    var peakHeight = _peakBars[i] * maxBarHeight;
                    var capYTop = centerY - peakHeight - capHeight - 2f;
                    var capYBottom = centerY + peakHeight + 2f;
                    canvas.DrawRect(new SKRect(x, capYTop, x + barWidth, capYTop + capHeight), _peakPaint);
                    canvas.DrawRect(new SKRect(x, capYBottom, x + barWidth, capYBottom + capHeight), _peakPaint);
                }
            }
        }

        private SKColor GetNeonColor(int index, int total)
        {
            var hue = (index * 3.0) + _colorTick;
            return _config.ColorPalette switch
            {
                ColorPalette.Fire => HslToRgb(hue * 0.8, 1.0, 0.55),
                ColorPalette.Ocean => HslToRgb(200 + hue * 0.3, 0.9, 0.55),
                ColorPalette.Mono => HslToRgb(0, 0, 0.8),
                ColorPalette.Purple => HslToRgb(270 + hue * 0.4, 0.9, 0.6),
                _ => HslToRgb(hue, 1.0, 0.55)
            };
        }

        private static SKColor HslToRgb(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            l = Math.Clamp(l, 0, 1);

            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            var m = l - c / 2;

            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            var r = (byte)Math.Clamp((r1 + m) * 255, 0, 255);
            var g = (byte)Math.Clamp((g1 + m) * 255, 0, 255);
            var b = (byte)Math.Clamp((b1 + m) * 255, 0, 255);
            return new SKColor(r, g, b);
        }

        /// <summary>
        /// Draw waveform visualization (oscilloscope style).
        /// </summary>
        private void DrawWaveform(SKCanvas canvas, int width, int height)
        {
            var centerY = height / 2f;
            var pointsPerBand = width / (float)_config.BandCount;
            var maxAmplitude = height * 0.45f;

            // Use visible neon color (colorGradient[0] is often black; invisible on black bg)
            _wavePaint.Color = GetNeonColor(0, _config.BandCount);

            _wavePath.Reset();
            _wavePath.MoveTo(0, centerY);

            for (int i = 0; i < _config.BandCount; i++)
            {
                var value = Math.Clamp(_currentSpectrum[i], 0f, 1f);
                var x = i * pointsPerBand;
                var y = centerY - (value - 0.5f) * maxAmplitude * 2f;
                _wavePath.LineTo(x, y);
            }

            _wavePath.LineTo(width, centerY);
            canvas.DrawPath(_wavePath, _wavePaint);
        }

        /// <summary>
        /// Draw circular/radial visualization.
        /// </summary>
        private void DrawCircular(SKCanvas canvas, int width, int height)
        {
            var centerX = width / 2f;
            var centerY = height / 2f;
            var baseRadius = Math.Min(width, height) * 0.25f;
            var maxRadiusExtent = Math.Min(width, height) * 0.35f;

            var colorGradient = GetColorGradient(_config.ColorPalette);

            for (int i = 0; i < _config.BandCount; i++)
            {
                var angle = (float)(i * 2 * Math.PI / _config.BandCount);
                var value = Math.Clamp(_currentSpectrum[i], 0f, 1f);
                var radius = baseRadius + value * maxRadiusExtent;

                var x = centerX + radius * MathF.Cos(angle);
                var y = centerY + radius * MathF.Sin(angle);

                _circularPaint.Color = colorGradient[i % colorGradient.Length];
                canvas.DrawLine(centerX, centerY, x, y, _circularPaint);
            }
        }

        /// <summary>
        /// Get color gradient for the specified palette.
        /// </summary>
        private SKColor[] GetColorGradient(ColorPalette palette)
        {
            return palette switch
            {
                ColorPalette.Rainbow => new[]
                {
                    SKColor.Parse("#FF0000"), // Red
                    SKColor.Parse("#FF7F00"), // Orange
                    SKColor.Parse("#FFFF00"), // Yellow
                    SKColor.Parse("#00FF00"), // Green
                    SKColor.Parse("#0000FF"), // Blue
                    SKColor.Parse("#4B0082"), // Indigo
                    SKColor.Parse("#9400D3")  // Violet
                },

                ColorPalette.Fire => new[]
                {
                    SKColor.Parse("#000000"), // Black
                    SKColor.Parse("#FF0000"), // Red
                    SKColor.Parse("#FF7F00"), // Orange
                    SKColor.Parse("#FFFF00"), // Yellow
                    SKColor.Parse("#FFFFFF")  // White
                },

                ColorPalette.Ocean => new[]
                {
                    SKColor.Parse("#000000"), // Black
                    SKColor.Parse("#000080"), // Navy
                    SKColor.Parse("#0000FF"), // Blue
                    SKColor.Parse("#00FFFF"), // Cyan
                    SKColor.Parse("#FFFFFF")  // White
                },

                ColorPalette.Purple => new[]
                {
                    SKColor.Parse("#000000"), // Black
                    SKColor.Parse("#4B0082"), // Indigo
                    SKColor.Parse("#800080"), // Purple
                    SKColor.Parse("#FF00FF"), // Magenta
                    SKColor.Parse("#FFFFFF")  // White
                },

                _ => new[] // Mono
                {
                    SKColor.Parse("#000000"), // Black
                    SKColor.Parse("#808080"), // Gray
                    SKColor.Parse("#FFFFFF")  // White
                }
            };
        }

        /// <summary>
        /// Get the current rendered bitmap.
        /// </summary>
        public SKBitmap? GetCurrentBitmap()
        {
            lock (_bitmapLock)
            {
                return _currentBitmap;
            }
        }

        private void EnsureBitmap(int width, int height)
        {
            lock (_bitmapLock)
            {
                if (_currentBitmap == null || _currentBitmap.Width != width || _currentBitmap.Height != height)
                {
                    _currentBitmap?.Dispose();
                    _currentBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
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
            _currentBitmap?.Dispose();
            _wavePath.Dispose();
            _barPaint.Dispose();
            _peakPaint.Dispose();
            _linePaint.Dispose();
            _wavePaint.Dispose();
            _circularPaint.Dispose();
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
