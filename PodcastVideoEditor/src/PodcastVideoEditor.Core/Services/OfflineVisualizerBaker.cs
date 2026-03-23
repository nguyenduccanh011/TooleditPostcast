#nullable enable
using NAudio.Dsp;
using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Visualizers;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Bakes a VisualizerElement's spectrum animation to a transparent RGBA video file
/// suitable for overlaying in the FFmpeg render pipeline.
///
/// Algorithm:
///   1. Opens the project audio with NAudio AudioFileReader (supports MP3, WAV, etc.)
///   2. Uses a sliding ring-buffer to extract per-frame FFT windows (same approach as
///      SampleAggregator used in the live preview).
///   3. Processes spectrum data identically to VisualizerService (auto-gain, power
///      curve, exponential smoothing, peak hold).
///   4. Renders each frame with VisualizerRendererRegistry onto a transparent
///      SKBitmap then pipes the raw RGBA bytes to an FFmpeg child process.
///   5. FFmpeg encodes the frames as a lossless PNG-in-MOV container (-c:v png
///      -pix_fmt rgba) which preserves the alpha channel for the overlay step.
/// </summary>
public static class OfflineVisualizerBaker
{
    private const float PowerCurveExponent = 0.7f;
    private const float PeakDecayRate      = 0.05f;
    private const double ColorTickActive   = 0.15;

    /// <summary>
    /// Bake a <see cref="VisualizerElement"/> to a transparent video file.
    /// </summary>
    /// <param name="element">The visualizer element (style, palette, band count, position).</param>
    /// <param name="audioFilePath">Path to the project's primary audio file.</param>
    /// <param name="renderWidth">Final render output width in pixels.</param>
    /// <param name="renderHeight">Final render output height in pixels.</param>
    /// <param name="canvasWidth">Canvas preview width used for coordinate mapping.</param>
    /// <param name="canvasHeight">Canvas preview height used for coordinate mapping.</param>
    /// <param name="startTime">Timeline start time of the visualizer in seconds.</param>
    /// <param name="endTime">Timeline end time of the visualizer in seconds.</param>
    /// <param name="fps">Frames per second for the baked output.</param>
    /// <param name="ffmpegPath">Absolute path to the ffmpeg executable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Path to the baked .mov file, or <c>null</c> if baking failed or was skipped.
    /// </returns>
    public static async Task<string?> BakeAsync(
        VisualizerElement element,
        string audioFilePath,
        int renderWidth,
        int renderHeight,
        int canvasWidth,
        int canvasHeight,
        double startTime,
        double endTime,
        int fps,
        string ffmpegPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(audioFilePath))
        {
            Log.Warning("OfflineVisualizerBaker: audio file not found ({Path}), skipping element {Id}",
                audioFilePath, element.Id);
            return null;
        }

        if (!File.Exists(ffmpegPath))
        {
            Log.Warning("OfflineVisualizerBaker: ffmpeg not found ({Path}), skipping visualizer bake",
                ffmpegPath);
            return null;
        }

        var duration = endTime - startTime;
        if (duration <= 0)
        {
            Log.Warning("OfflineVisualizerBaker: invalid time range [{Start},{End}] for element {Id}",
                startTime, endTime, element.Id);
            return null;
        }

        // ── Output dimensions at render scale ───────────────────────────────
        var scaleX = canvasWidth  > 0 ? renderWidth  / (double)canvasWidth  : 1.0;
        var scaleY = canvasHeight > 0 ? renderHeight / (double)canvasHeight : 1.0;

        int outW = Math.Max(16, (int)Math.Round(element.Width  * scaleX));
        int outH = Math.Max(16, (int)Math.Round(element.Height * scaleY));

        // ── Build VisualizerConfig from element ─────────────────────────────
        var config = new VisualizerConfig
        {
            BandCount        = element.BandCount,
            Style            = element.Style,
            ColorPalette     = element.ColorPalette,
            SmoothingFactor  = 0.6f,
            ShowPeaks        = true,
            PeakHoldTime     = 300,
            SymmetricMode    = true
        };

        // ── Prepare temp output file ────────────────────────────────────────
        var tempDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "visualizer_bake");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir,
            $"viz_{element.Id}_{DateTime.Now:yyyyMMddHHmmssff}.mov");

        Log.Information(
            "OfflineVisualizerBaker: baking element {Id} ({Style}/{Palette}/{BandCount}b) " +
            "to {OutW}x{OutH}@{Fps}fps  duration={Duration:F2}s  →  {Output}",
            element.Id, element.Style, element.ColorPalette, element.BandCount,
            outW, outH, fps, duration, outputPath);

        try
        {
            await BakeCoreAsync(
                config, audioFilePath, startTime, endTime,
                outW, outH, fps, ffmpegPath, outputPath, ct);

            if (!File.Exists(outputPath))
            {
                Log.Error("OfflineVisualizerBaker: output file not created for element {Id}", element.Id);
                return null;
            }

            Log.Information("OfflineVisualizerBaker: bake completed → {Path}", outputPath);
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            Log.Information("OfflineVisualizerBaker: bake cancelled for element {Id}", element.Id);
            TryDelete(outputPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OfflineVisualizerBaker: bake failed for element {Id}", element.Id);
            TryDelete(outputPath);
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Core bake routine
    // ────────────────────────────────────────────────────────────────────────

    private static async Task BakeCoreAsync(
        VisualizerConfig config,
        string audioFilePath,
        double startTime,
        double endTime,
        int outW,
        int outH,
        int fps,
        string ffmpegPath,
        string outputPath,
        CancellationToken ct)
    {
        // FFmpeg: consume raw RGBA bytes from stdin → lossless PNG-in-MOV with alpha
        var ffmpegArgs =
            $"-y " +
            $"-f rawvideo -pixel_format rgba -video_size {outW}x{outH} -framerate {fps} -i pipe:0 " +
            $"-c:v png -pix_fmt rgba " +
            $"\"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpegPath, ffmpegArgs)
        {
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var ffmpegProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg for visualizer bake");

        // Drain stderr asynchronously to avoid deadlock
        _ = ffmpegProcess.StandardError.ReadToEndAsync();

        try
        {
            await GenerateAndPipeFramesAsync(
                config, audioFilePath, startTime, endTime,
                outW, outH, fps,
                ffmpegProcess.StandardInput.BaseStream, ct);
        }
        finally
        {
            try { ffmpegProcess.StandardInput.BaseStream.Close(); } catch { /* ignore */ }
        }

        await ffmpegProcess.WaitForExitAsync(ct);

        if (ffmpegProcess.ExitCode != 0)
            throw new Exception(
                $"FFmpeg exited with code {ffmpegProcess.ExitCode} while baking visualizer");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Frame generation loop
    // ────────────────────────────────────────────────────────────────────────

    private static async Task GenerateAndPipeFramesAsync(
        VisualizerConfig config,
        string audioFilePath,
        double startTime,
        double endTime,
        int outW,
        int outH,
        int fps,
        Stream pipeStream,
        CancellationToken ct)
    {
        int fftSize       = NextPow2(config.BandCount * 8);
        double duration   = endTime - startTime;
        int totalFrames   = (int)Math.Ceiling(duration * fps);

        // ── Open audio ───────────────────────────────────────────────────────
        using var audioReader = new AudioFileReader(audioFilePath);

        int sampleRate = audioReader.WaveFormat.SampleRate;
        int channels   = audioReader.WaveFormat.Channels;
        int samplesPerFrame = Math.Max(1, sampleRate / fps); // mono samples per frame

        // Seek to startTime
        if (startTime > 0)
            audioReader.CurrentTime = TimeSpan.FromSeconds(startTime);

        // ── Spectrum state ────────────────────────────────────────────────────
        var ringBuffer     = new float[fftSize];    // mono ring buffer for FFT
        int ringWrite      = 0;
        var currentSpec    = new float[config.BandCount];
        var previousSpec   = new float[config.BandCount];
        var peakBars       = new float[config.BandCount];
        var peakHoldTimes  = new long[config.BandCount];
        double colorTick   = 0;

        // Read buffer: interleaved stereo samples from NAudio
        var readBuf = new float[samplesPerFrame * channels];

        // ── Renderer ─────────────────────────────────────────────────────────
        using var registry = new VisualizerRendererRegistry();
        using var bitmap   = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);

        // Frame byte buffer (reuse across frames to avoid GC pressure)
        var frameBytes = new byte[outW * outH * 4];

        for (int frameIdx = 0; frameIdx < totalFrames; frameIdx++)
        {
            ct.ThrowIfCancellationRequested();

            // ── Read audio samples for this frame ─────────────────────────
            int stereoSamplesToRead = samplesPerFrame * channels;
            int readCount = audioReader.Read(readBuf, 0, stereoSamplesToRead);

            // Mix down to mono and push into ring buffer
            int monoRead = readCount / channels;
            for (int i = 0; i < monoRead; i++)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                    mono += readBuf[i * channels + c];
                mono /= channels;

                ringBuffer[ringWrite] = mono;
                ringWrite = (ringWrite + 1) % fftSize;
            }

            // ── Compute FFT ────────────────────────────────────────────────
            var magnitudes = ComputeFFTMagnitudes(ringBuffer, ringWrite, fftSize);

            // ── Process spectrum ───────────────────────────────────────────
            ProcessSpectrum(magnitudes, currentSpec, previousSpec,
                            peakBars, peakHoldTimes, config);

            // Build symmetric or raw arrays
            float[] specToRender;
            float[] peaksToRender;
            if (config.SymmetricMode)
                specToRender = BuildMirroredSpectrum(currentSpec, peakBars, out peaksToRender);
            else
            {
                specToRender  = currentSpec;
                peaksToRender = peakBars;
            }

            // ── Render frame ────────────────────────────────────────────────
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.DrawColor(SKColors.Transparent, SKBlendMode.Src);
                registry.GetRenderer(config.Style)
                        .Render(canvas, specToRender, peaksToRender, config,
                                outW, outH, colorTick);
            }

            colorTick += ColorTickActive;

            // ── Copy bitmap bytes and pipe to FFmpeg ────────────────────────
            CopyBitmapBytes(bitmap, frameBytes);
            await pipeStream.WriteAsync(frameBytes, 0, frameBytes.Length, ct);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Spectrum processing  (mirrors VisualizerService logic exactly)
    // ────────────────────────────────────────────────────────────────────────

    private static float[] ComputeFFTMagnitudes(float[] ringBuffer, int writeIndex, int fftSize)
    {
        var complex = new Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            int idx = (writeIndex - fftSize + i + ringBuffer.Length) % ringBuffer.Length;
            var sample = ringBuffer[idx];
            // Hann window
            var window = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
            complex[i].X = sample * window;
            complex[i].Y = 0f;
        }

        int m = (int)Math.Log2(fftSize);
        FastFourierTransform.FFT(true, m, complex);

        var magnitudes = new float[fftSize / 2];
        for (int i = 0; i < magnitudes.Length; i++)
        {
            magnitudes[i] = (float)Math.Sqrt(
                complex[i].X * complex[i].X + complex[i].Y * complex[i].Y);
        }
        return magnitudes;
    }

    private static void ProcessSpectrum(
        float[] magnitudes,
        float[] current,
        float[] previous,
        float[] peaks,
        long[] peakHoldTimes,
        VisualizerConfig config)
    {
        int bandCount = config.BandCount;
        int maxIndex  = Math.Max(1, (int)(magnitudes.Length * 0.6f));
        float maxVal  = 0f;

        for (int i = 0; i < maxIndex; i++)
            if (magnitudes[i] > maxVal) maxVal = magnitudes[i];

        if (maxVal <= 1e-6f)
        {
            // Silence — decay gently toward zero
            for (int i = 0; i < bandCount; i++)
                current[i] = Lerp(previous[i], 0f, 1f - config.SmoothingFactor);
            Array.Copy(current, previous, bandCount);
            return;
        }

        long now = Environment.TickCount64;
        for (int i = 0; i < bandCount; i++)
        {
            int start = (int)(i       * (maxIndex / (float)bandCount));
            int end   = (int)((i + 1) * (maxIndex / (float)bandCount));
            if (end <= start) end = Math.Min(maxIndex, start + 1);

            float sum = 0f;
            for (int j = start; j < end; j++) sum += magnitudes[j];
            float avg      = sum / (end - start);
            float rawVal   = avg / maxVal;
            float newValue = MathF.Pow(Math.Clamp(rawVal, 0f, 1f), PowerCurveExponent);

            current[i] = Lerp(previous[i], newValue, 1f - config.SmoothingFactor);

            if (current[i] > peaks[i])
            {
                peaks[i]         = current[i];
                peakHoldTimes[i] = now;
            }
            else if (now - peakHoldTimes[i] > config.PeakHoldTime)
            {
                peaks[i] = Lerp(peaks[i], 0f, PeakDecayRate);
            }
        }

        Array.Copy(current, previous, bandCount);
    }

    private static float[] BuildMirroredSpectrum(
        float[] spectrum, float[] peaks, out float[] mirroredPeaks)
    {
        int total       = spectrum.Length;
        int activeBands = (int)(total * 0.6f);
        int half        = total / 2;

        var result = new float[total];
        mirroredPeaks   = new float[total];

        for (int i = 0; i < half; i++)
        {
            int bandIdx = activeBands - 1 - (int)(i * activeBands / (float)half);
            bandIdx = Math.Clamp(bandIdx, 0, activeBands - 1);

            result[i]                 = spectrum[bandIdx];
            result[total - 1 - i]     = spectrum[bandIdx];
            mirroredPeaks[i]          = peaks[bandIdx];
            mirroredPeaks[total-1-i]  = peaks[bandIdx];
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Copies RGBA pixel bytes out of a SKBitmap into a pre-allocated byte array.</summary>
    private static void CopyBitmapBytes(SKBitmap bitmap, byte[] dest)
    {
        var ptr = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(ptr, dest, 0, bitmap.ByteCount);
    }

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * Math.Clamp(t, 0f, 1f);

    private static int NextPow2(int v)
    {
        if (v <= 0) return 1;
        v--;
        v |= v >> 1;  v |= v >> 2;  v |= v >> 4;
        v |= v >> 8;  v |= v >> 16;
        return v + 1;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
