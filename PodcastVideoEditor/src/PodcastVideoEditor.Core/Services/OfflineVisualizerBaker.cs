#nullable enable
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
///   3. Delegates spectrum processing to <see cref="SpectrumProcessor"/> (auto-gain,
///      power curve, exponential smoothing, peak hold).
///   4. Renders each frame with VisualizerRendererRegistry onto a transparent
///      SKBitmap then pipes the raw RGBA bytes to an FFmpeg child process.
///   5. FFmpeg encodes the frames as a lossless PNG-in-MOV container (-c:v png
///      -pix_fmt rgba) which preserves the alpha channel for the overlay step.
/// </summary>
public static class OfflineVisualizerBaker
{
    private const double ColorTickActive   = 0.15;
    private const int    SilenceFrameThreshold  = 3;   // switch to demo after this many silent frames
    private const float  SilenceLevel           = 0.005f;

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
            SmoothingFactor  = element.SmoothingFactor,
            ShowPeaks        = element.ShowPeaks,
            PeakHoldTime     = 300,
            SymmetricMode    = element.SymmetricMode
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

        // Capture stderr asynchronously (must drain to prevent deadlock)
        var stderrTask = ffmpegProcess.StandardError.ReadToEndAsync();

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
        var stderrOutput = await stderrTask;

        if (ffmpegProcess.ExitCode != 0)
        {
            Log.Error("OfflineVisualizerBaker: FFmpeg stderr:\n{Stderr}", stderrOutput);
            throw new Exception(
                $"FFmpeg exited with code {ffmpegProcess.ExitCode} while baking visualizer");
        }

        // Log last few lines of stderr at Debug level for diagnosis even on success
        if (!string.IsNullOrWhiteSpace(stderrOutput))
        {
            var lastLines = stderrOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var summary = string.Join("\n", lastLines.TakeLast(5));
            Log.Debug("OfflineVisualizerBaker: FFmpeg completed (last lines):\n{Summary}", summary);
        }
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
        int fftSize       = SpectrumProcessor.NextPow2(config.BandCount * 8);
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

        // ── Spectrum state via shared SpectrumProcessor ───────────────────────
        var ringBuffer = new float[fftSize];    // mono ring buffer for FFT
        int ringWrite  = 0;
        var specProcessor = new SpectrumProcessor(config.BandCount);
        double colorTick  = 0;
        int silentFrames  = 0;  // consecutive frames with near-zero spectrum

        // Read buffer: interleaved stereo samples from NAudio
        var readBuf = new float[samplesPerFrame * channels];

        // ── Renderer ─────────────────────────────────────────────────────────
        using var registry = new VisualizerRendererRegistry();
        // SKAlphaType.Unpremul → bytes are written as straight (non-premultiplied) RGBA,
        // which is what FFmpeg's -pixel_format rgba expects for correct alpha compositing.
        using var bitmap   = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);

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

            // ── Compute FFT → process spectrum ────────────────────────────
            var magnitudes = SpectrumProcessor.ComputeFFTMagnitudes(ringBuffer, ringWrite, fftSize);
            specProcessor.ProcessSpectrum(magnitudes, config);

            // ── Demo fallback: replace silent frames with animated idle spectrum ──
            if (specProcessor.IsSilent(SilenceLevel))
            {
                silentFrames++;
                if (silentFrames >= SilenceFrameThreshold)
                    specProcessor.GenerateDemoSpectrum(config, startTime + frameIdx / (double)fps);
            }
            else
            {
                silentFrames = 0;
            }

            // Build symmetric or raw arrays
            float[] specToRender;
            float[] peaksToRender;
            if (config.SymmetricMode)
                specToRender = specProcessor.BuildMirroredSpectrum(out peaksToRender);
            else
            {
                specToRender  = specProcessor.CurrentSpectrum;
                peaksToRender = specProcessor.PeakBars;
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
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static void CopyBitmapBytes(SKBitmap bitmap, byte[] dest)
    {
        var ptr = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(ptr, dest, 0, bitmap.ByteCount);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
