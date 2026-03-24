#nullable enable
using PodcastVideoEditor.Core.Models;
using System;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Shared spectrum processing logic used by both the live preview (<see cref="VisualizerService"/>)
/// and the offline bake pipeline (<see cref="OfflineVisualizerBaker"/>).
/// Holds per-frame state (current/previous spectrum, peak bars, peak hold times) and exposes
/// methods for FFT processing, smoothing, peak hold, demo/idle animation, and mirroring.
/// </summary>
public sealed class SpectrumProcessor
{
    // ── Processing constants ─────────────────────────────────────────────
    public const float PowerCurveExponent = 0.7f;
    public const float PeakDecayRate = 0.05f;

    // ── Idle / demo mode constants ───────────────────────────────────────
    public const float IdleBandFreqBase = 0.25f;
    public const float IdleBandFreqRange = 1.0f;
    public const float IdleBaseAmplitude = 0.05f;
    public const float IdleWave1Amplitude = 0.03f;
    public const float IdleWave2Amplitude = 0.02f;
    public const float IdleWave3Amplitude = 0.01f;
    public const float IdleFreqDamping = 0.3f;

    // ── State ────────────────────────────────────────────────────────────
    private float[] _currentSpectrum;
    private float[] _previousSpectrum;
    private float[] _peakBars;
    private long[] _peakHoldTimes;
    private int _bandCount;

    /// <summary>Current processed spectrum values (0..1 per band).</summary>
    public float[] CurrentSpectrum => _currentSpectrum;

    /// <summary>Current peak bar values (0..1 per band).</summary>
    public float[] PeakBars => _peakBars;

    /// <summary>Number of bands this processor was initialised with.</summary>
    public int BandCount => _bandCount;

    public SpectrumProcessor(int bandCount)
    {
        _bandCount = bandCount;
        _currentSpectrum = new float[bandCount];
        _previousSpectrum = new float[bandCount];
        _peakBars = new float[bandCount];
        _peakHoldTimes = new long[bandCount];
    }

    /// <summary>
    /// Re-initialise all buffers (e.g. when band count changes).
    /// </summary>
    public void Reinitialize(int bandCount)
    {
        _bandCount = bandCount;
        _currentSpectrum = new float[bandCount];
        _previousSpectrum = new float[bandCount];
        _peakBars = new float[bandCount];
        _peakHoldTimes = new long[bandCount];
    }

    // ─────────────────────────────────────────────────────────────────────
    // Spectrum processing  (auto-gain → power curve → smoothing → peak hold)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Process raw FFT magnitude data into smoothed spectrum bands with peak hold.
    /// Handles the full pipeline: auto-gain normalise, power curve boost, exponential
    /// smoothing, and peak hold/decay. When input is silent the spectrum decays gently.
    /// </summary>
    /// <param name="timestampMs">Optional simulated timestamp in milliseconds.
    /// When null, uses <see cref="Environment.TickCount64"/> (wall-clock).  Offline
    /// bakers should pass a virtual timestamp so peak decay works correctly even when
    /// frames are processed faster than real-time.</param>
    public void ProcessSpectrum(float[] magnitudes, VisualizerConfig config, long? timestampMs = null)
    {
        int bandCount = config.BandCount;
        int maxIndex = Math.Max(1, (int)(magnitudes.Length * 0.6f));
        float maxVal = 0f;

        for (int i = 0; i < maxIndex; i++)
            if (magnitudes[i] > maxVal) maxVal = magnitudes[i];

        if (maxVal <= 1e-6f)
        {
            // Silence — decay gently toward zero
            for (int i = 0; i < bandCount; i++)
                _currentSpectrum[i] = Lerp(_previousSpectrum[i], 0f, 1f - config.SmoothingFactor);
            Array.Copy(_currentSpectrum, _previousSpectrum, bandCount);
            return;
        }

        long now = timestampMs ?? Environment.TickCount64;
        for (int i = 0; i < bandCount; i++)
        {
            int start = (int)(i * (maxIndex / (float)bandCount));
            int end = (int)((i + 1) * (maxIndex / (float)bandCount));
            if (end <= start) end = Math.Min(maxIndex, start + 1);

            float sum = 0f;
            for (int j = start; j < end; j++) sum += magnitudes[j];
            float avg = sum / (end - start);
            float rawVal = avg / maxVal;
            float newValue = MathF.Pow(Math.Clamp(rawVal, 0f, 1f), PowerCurveExponent);

            _currentSpectrum[i] = Lerp(_previousSpectrum[i], newValue, 1f - config.SmoothingFactor);

            if (_currentSpectrum[i] > _peakBars[i])
            {
                _peakBars[i] = _currentSpectrum[i];
                _peakHoldTimes[i] = now;
            }
            else if (now - _peakHoldTimes[i] > config.PeakHoldTime)
            {
                _peakBars[i] = Lerp(_peakBars[i], 0f, PeakDecayRate);
            }
        }

        Array.Copy(_currentSpectrum, _previousSpectrum, bandCount);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Demo / idle animation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a time-varying synthetic idle spectrum.
    /// Very low amplitude standing waves (~10% bar height) so users can see
    /// the visualizer is alive without mistaking it for real audio.
    /// </summary>
    public void GenerateDemoSpectrum(VisualizerConfig config, double timeSeconds, long? timestampMs = null)
    {
        long now = timestampMs ?? Environment.TickCount64;
        int bandCount = config.BandCount;
        for (int i = 0; i < bandCount; i++)
        {
            var freq = i / (float)bandCount;
            var bandFreq = IdleBandFreqBase + freq * IdleBandFreqRange;
            var val = IdleBaseAmplitude
                      + IdleWave1Amplitude * MathF.Sin((float)(timeSeconds * bandFreq))
                      + IdleWave2Amplitude * MathF.Sin((float)(timeSeconds * bandFreq * 1.7))
                      + IdleWave3Amplitude * MathF.Sin((float)(timeSeconds * bandFreq * 0.5));
            val *= 1.0f - freq * IdleFreqDamping;

            var newValue = Math.Clamp(val, 0f, 1f);
            _currentSpectrum[i] = Lerp(_previousSpectrum[i], newValue, 1f - config.SmoothingFactor);

            if (_currentSpectrum[i] > _peakBars[i])
            {
                _peakBars[i] = _currentSpectrum[i];
                _peakHoldTimes[i] = now;
            }
            else if (now - _peakHoldTimes[i] > config.PeakHoldTime)
            {
                _peakBars[i] = Lerp(_peakBars[i], 0f, PeakDecayRate);
            }
        }
        Array.Copy(_currentSpectrum, _previousSpectrum, bandCount);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mirroring
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a symmetric spectrum from the lowest 60% of bands.
    /// Left half: low→mid (outside→center). Right half: mirror.
    /// Creates a visually appealing butterfly display with energy concentrated in the centre.
    /// </summary>
    public float[] BuildMirroredSpectrum(out float[] mirroredPeaks)
    {
        int total = _currentSpectrum.Length;
        int activeBands = (int)(total * 0.6f);
        int half = total / 2;

        var result = new float[total];
        mirroredPeaks = new float[total];

        for (int i = 0; i < half; i++)
        {
            int bandIdx = activeBands - 1 - (int)(i * activeBands / (float)half);
            bandIdx = Math.Clamp(bandIdx, 0, activeBands - 1);

            result[i] = _currentSpectrum[bandIdx];
            result[total - 1 - i] = _currentSpectrum[bandIdx];
            mirroredPeaks[i] = _peakBars[bandIdx];
            mirroredPeaks[total - 1 - i] = _peakBars[bandIdx];
        }
        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when all current spectrum values are below <paramref name="threshold"/>.
    /// </summary>
    public bool IsSilent(float threshold)
    {
        for (int i = 0; i < _bandCount; i++)
            if (_currentSpectrum[i] > threshold) return false;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Static helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute FFT magnitudes from a ring buffer using Hann windowing.
    /// </summary>
    public static float[] ComputeFFTMagnitudes(float[] ringBuffer, int writeIndex, int fftSize)
    {
        var complex = new NAudio.Dsp.Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            int idx = (writeIndex - fftSize + i + ringBuffer.Length) % ringBuffer.Length;
            var sample = ringBuffer[idx];
            var window = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
            complex[i].X = sample * window;
            complex[i].Y = 0f;
        }

        int m = (int)Math.Log2(fftSize);
        NAudio.Dsp.FastFourierTransform.FFT(true, m, complex);

        var magnitudes = new float[fftSize / 2];
        for (int i = 0; i < magnitudes.Length; i++)
        {
            magnitudes[i] = (float)Math.Sqrt(
                complex[i].X * complex[i].X + complex[i].Y * complex[i].Y);
        }
        return magnitudes;
    }

    /// <summary>Linear interpolation clamped to [0,1].</summary>
    public static float Lerp(float a, float b, float t) =>
        a + (b - a) * Math.Clamp(t, 0f, 1f);

    /// <summary>Round up to the next power of 2.</summary>
    public static int NextPow2(int v)
    {
        if (v <= 0) return 1;
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4;
        v |= v >> 8; v |= v >> 16;
        return v + 1;
    }
}
