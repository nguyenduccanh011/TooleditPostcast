#nullable enable
using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Visualizers;
using Serilog;
using SkiaSharp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// Live spectrum visualizer painter. Precomputes the per-frame spectrum for the element's time
/// range ONCE on a background thread (sequential read — no per-frame seeking), then each Paint
/// call is an O(1) lookup + draw. This keeps playback/scrub smooth (the per-frame audio seek of a
/// naive live reader was the cause of stutter). Draws with the same SkiaSharp renderers the
/// offline bake uses, so the preview matches export.
/// </summary>
public sealed class VisualizerCompositorLayerPainter : IVisualizerLayerPainter, IDisposable
{
    private const float SilenceLevel = 0.005f;
    private const int SilenceFrameThreshold = 3;
    private const double ColorTickActive = 0.15;

    private readonly string _audioPath;
    private readonly VisualizerConfig _config;
    private readonly double _elementStart;
    private readonly double _elementEnd;
    private readonly int _analysisFps;
    private readonly int _totalFrames;

    private readonly float[]?[] _frames;
    private readonly float[]?[] _peakFrames;
    private int _frameCount; // number of precomputed frames (Volatile)

    private readonly SpectrumProcessor _demoProcessor;
    private readonly VisualizerRendererRegistry _registry;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.ManualResetEventSlim _precomputeDone = new(false);
    private double _colorTick;
    private bool _disposed;

    /// <summary>Blocks until the spectrum precompute finishes (or the timeout elapses). Used by export.</summary>
    public bool WaitUntilReady(int timeoutMs)
    {
        try { return _precomputeDone.Wait(timeoutMs, _cts.Token); }
        catch { return false; }
    }

    public VisualizerCompositorLayerPainter(string audioFilePath, VisualizerConfig config, double elementStart, double elementEnd, int analysisFps)
    {
        _audioPath = audioFilePath ?? throw new ArgumentNullException(nameof(audioFilePath));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _elementStart = elementStart;
        _elementEnd = elementEnd;
        _analysisFps = Math.Clamp(analysisFps, 10, 60);

        var seconds = Math.Max(0.1, _elementEnd - _elementStart);
        _totalFrames = Math.Clamp((int)Math.Ceiling(seconds * _analysisFps), 1, _analysisFps * 3600);
        _frames = new float[_totalFrames][];
        _peakFrames = new float[_totalFrames][];

        _demoProcessor = new SpectrumProcessor(_config.BandCount);
        _registry = new VisualizerRendererRegistry();

        _ = Task.Run(() => PrecomputeAsync(_cts.Token));
    }

    private async Task PrecomputeAsync(CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            using var reader = new AudioFileReader(_audioPath);
            int sampleRate = reader.WaveFormat.SampleRate;
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            int fftSize = SpectrumProcessor.NextPow2(Math.Max(8, _config.BandCount) * 8);
            var ring = new float[fftSize];
            int ringWrite = 0;
            var proc = new SpectrumProcessor(_config.BandCount);
            int samplesPerFrame = Math.Max(1, sampleRate / _analysisFps);
            var readBuf = new float[samplesPerFrame * channels];

            if (_elementStart > 0)
                reader.CurrentTime = TimeSpan.FromSeconds(_elementStart);

            int silent = 0;
            for (int f = 0; f < _totalFrames; f++)
            {
                ct.ThrowIfCancellationRequested();

                int readCount = reader.Read(readBuf, 0, readBuf.Length);
                if (readCount == 0)
                    break; // EOF — remaining frames fall back to demo

                int monoRead = readCount / channels;
                for (int i = 0; i < monoRead; i++)
                {
                    float m = 0f;
                    for (int c = 0; c < channels; c++)
                        m += readBuf[i * channels + c];
                    m /= channels;
                    ring[ringWrite] = m;
                    ringWrite = (ringWrite + 1) % fftSize;
                }

                var mags = SpectrumProcessor.ComputeFFTMagnitudes(ring, ringWrite, fftSize);
                long simMs = (long)((_elementStart + f / (double)_analysisFps) * 1000.0);
                proc.ProcessSpectrum(mags, _config, simMs);

                if (proc.IsSilent(SilenceLevel))
                {
                    silent++;
                    if (silent >= SilenceFrameThreshold)
                        proc.GenerateDemoSpectrum(_config, _elementStart + f / (double)_analysisFps, simMs);
                }
                else
                {
                    silent = 0;
                }

                float[] spec, peaks;
                if (_config.SymmetricMode)
                {
                    spec = proc.BuildMirroredSpectrum(out peaks);
                    spec = (float[])spec.Clone();
                    peaks = (float[])peaks.Clone();
                }
                else
                {
                    spec = (float[])proc.CurrentSpectrum.Clone();
                    peaks = (float[])proc.PeakBars.Clone();
                }

                _frames[f] = spec;
                _peakFrames[f] = peaks;
                Volatile.Write(ref _frameCount, f + 1);
            }

            Log.Information("Visualizer precompute done: {Frames} frames @ {Fps}fps", Volatile.Read(ref _frameCount), _analysisFps);
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (Exception ex)
        {
            Log.Debug(ex, "Visualizer precompute failed");
        }
        finally
        {
            _precomputeDone.Set();
        }
    }

    public void Paint(SKCanvas canvas, int width, int height, double timeSeconds)
    {
        if (_disposed || width <= 0 || height <= 0)
            return;

        int idx = (int)Math.Round((timeSeconds - _elementStart) * _analysisFps);
        int ready = Volatile.Read(ref _frameCount);

        float[]? spec = null;
        float[]? peaks = null;
        if (idx >= 0 && idx < ready)
        {
            spec = _frames[idx];
            peaks = _peakFrames[idx];
        }

        if (spec == null || peaks == null)
        {
            // Not computed yet (precompute still catching up) or out of range — animated demo.
            _demoProcessor.GenerateDemoSpectrum(_config, timeSeconds, (long)(timeSeconds * 1000));
            if (_config.SymmetricMode)
                spec = _demoProcessor.BuildMirroredSpectrum(out peaks);
            else
            {
                spec = _demoProcessor.CurrentSpectrum;
                peaks = _demoProcessor.PeakBars;
            }
        }

        _colorTick += ColorTickActive;

        try
        {
            _registry.GetRenderer(_config.Style).Render(canvas, spec, peaks, _config, width, height, _colorTick);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Visualizer layer paint failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        _precomputeDone.Set();
        _precomputeDone.Dispose();
        _cts.Dispose();
        _registry.Dispose();
    }
}
