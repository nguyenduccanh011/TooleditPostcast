#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Beta export that renders the timeline through the same Skia compositor used by the preview
/// (one composite pass per frame — no FFmpeg filter graph / bake / overlay-enable explosion),
/// pipes raw RGBA frames to a hardware encoder (NVENC) and muxes the project audio.
///
/// To make a long timeline practical, frames are split into contiguous time ranges rendered in
/// PARALLEL — each range composites on its own thread and encodes with its own NVENC session into
/// a chunk file; the chunks are then concatenated (stream copy) and the audio muxed in. Each range
/// uses isolated texture/visualizer sources (created via the factories) so there is no shared
/// mutable state across threads.
///
/// Limitation: embedded VIDEO clips are not yet frame-accurate (images/text/logos/visualizer are).
/// </summary>
public sealed class CompositorVideoExporter
{
    public sealed class Options
    {
        public required IReadOnlyList<RenderVisualSegment> Segments { get; init; }
        public required Func<ICompositorTextureSource> CreateTextures { get; init; }
        public Func<ICompositorVisualizerSource?>? CreateVisualizers { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int FrameRate { get; init; }
        public required double Duration { get; init; }
        public string? AudioPath { get; init; }
        public required string OutputPath { get; init; }
        public required string FfmpegPath { get; init; }
        public required string VideoEncoder { get; init; }
    }

    public async Task ExportAsync(Options o, IProgress<double>? progress, CancellationToken ct)
    {
        if (o.Width <= 0 || o.Height <= 0 || o.FrameRate <= 0)
            throw new ArgumentException("Invalid output dimensions/frame rate.");
        if (!File.Exists(o.FfmpegPath))
            throw new FileNotFoundException("FFmpeg not found.", o.FfmpegPath);

        var fps = o.FrameRate;
        var totalFrames = Math.Max(1, (int)Math.Ceiling(Math.Max(0.1, o.Duration) * fps));

        var cores = Environment.ProcessorCount;
        // NVENC consumer cards cap concurrent encode sessions (~3); keep within that.
        var degree = Math.Clamp(cores >= 8 ? 3 : 2, 1, 3);
        if (totalFrames < fps * 4) // very short → no point splitting
            degree = 1;

        var framesDone = 0;
        var reportLock = new object();
        void ReportFrame()
        {
            var done = Interlocked.Increment(ref framesDone);
            if (done % 5 == 0 || done == totalFrames)
            {
                lock (reportLock) progress?.Report((double)done / totalFrames);
            }
        }

        if (degree == 1)
        {
            await RenderRangeAsync(o, 0, totalFrames, o.OutputPath, includeAudio: true, ReportFrame, ct).ConfigureAwait(false);
            return;
        }

        // Partition frames into `degree` contiguous ranges (exact, no overlap/gap).
        var ranges = new List<(int startFrame, int endFrame)>();
        for (int i = 0; i < degree; i++)
        {
            int s = (int)((long)i * totalFrames / degree);
            int e = (int)((long)(i + 1) * totalFrames / degree);
            if (e > s) ranges.Add((s, e));
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "pve", $"cexport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var chunkPaths = new string[ranges.Count];

        try
        {
            Log.Information("Compositor export: {Frames} frames, parallel degree {Degree}, encoder {Enc}", totalFrames, degree, o.VideoEncoder);

            await Parallel.ForEachAsync(
                EnumerateIndexed(ranges),
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                async (item, token) =>
                {
                    var path = Path.Combine(tempDir, $"chunk_{item.index:D3}.mp4");
                    chunkPaths[item.index] = path;
                    await RenderRangeAsync(o, item.value.startFrame, item.value.endFrame, path, includeAudio: false, ReportFrame, token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            await ConcatAndMuxAsync(o, chunkPaths, o.OutputPath, totalFrames / (double)fps, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Log.Debug(ex, "Could not clean compositor export temp dir"); }
        }
    }

    private static IEnumerable<(int index, (int startFrame, int endFrame) value)> EnumerateIndexed(List<(int, int)> ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
            yield return (i, ranges[i]);
    }

    private async Task RenderRangeAsync(Options o, int startFrame, int endFrame, string outPath, bool includeAudio, Action onFrame, CancellationToken ct)
    {
        var fps = o.FrameRate;
        var frameCount = Math.Max(1, endFrame - startFrame);
        var rangeDuration = frameCount / (double)fps;
        var hasAudio = includeAudio && !string.IsNullOrWhiteSpace(o.AudioPath) && File.Exists(o.AudioPath);
        var inv = CultureInfo.InvariantCulture;

        var textures = o.CreateTextures();
        var visualizers = o.CreateVisualizers?.Invoke();
        // Ensure spectrum is fully precomputed before rendering so frames aren't demo.
        if (visualizers is Controls.VisualizerPreviewSource viz && viz.HasAny)
            viz.PrewarmAndWait(120_000);

        var presetArg = o.VideoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ? "-preset p4 -rc vbr -cq 23 "
            : o.VideoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase) ? "-preset fast "
            : o.VideoEncoder.Contains("amf", StringComparison.OrdinalIgnoreCase) ? "-quality balanced "
            : "-preset fast -crf 20 ";

        var args = new System.Text.StringBuilder();
        args.Append($"-y -f rawvideo -pixel_format rgba -video_size {o.Width}x{o.Height} -framerate {fps} -i pipe:0 ");
        if (hasAudio) args.Append($"-i \"{o.AudioPath}\" ");
        args.Append($"-c:v {o.VideoEncoder} {presetArg}-pix_fmt yuv420p -r {fps} ");
        args.Append(hasAudio ? "-c:a aac -b:a 192k -map 0:v:0 -map 1:a:0 " : "-map 0:v:0 ");
        args.Append($"-t {rangeDuration.ToString("F3", inv)} -movflags +faststart \"{outPath}\"");

        var psi = new ProcessStartInfo(o.FfmpegPath, args.ToString())
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(o.FfmpegPath) ?? ""
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
            var stderrLines = new List<string>();
            var localProc = proc;
            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await localProc.StandardError.ReadLineAsync()) != null)
                {
                    if (stderrLines.Count >= 120) stderrLines.RemoveAt(0);
                    stderrLines.Add(line);
                }
            });

            var frameBytes = o.Width * o.Height * 4;
            var buffer = new byte[frameBytes];
            var renderer = new SkiaSceneRenderer(textures) { Visualizers = visualizers };
            using var bmp = new SKBitmap(new SKImageInfo(o.Width, o.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            var stdin = proc.StandardInput.BaseStream;

            try
            {
                for (int lf = 0; lf < frameCount; lf++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = (startFrame + lf) / (double)fps;
                    var scene = CompositorSceneBuilder.BuildAt(o.Segments, o.Width, o.Height, fps, t);
                    renderer.Render(canvas, scene);
                    canvas.Flush();
                    Marshal.Copy(bmp.GetPixels(), buffer, 0, frameBytes);
                    await stdin.WriteAsync(buffer.AsMemory(0, frameBytes), ct).ConfigureAwait(false);
                    onFrame();
                }
            }
            finally
            {
                try { stdin.Close(); } catch { /* ignore */ }
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var tail = string.Join("\n", stderrLines.Count > 12 ? stderrLines.GetRange(stderrLines.Count - 12, 12) : stderrLines);
                Log.Error("Compositor export chunk failed (exit {Code}):\n{Tail}", proc.ExitCode, tail);
                throw new InvalidOperationException($"Export failed (ffmpeg exit {proc.ExitCode}).");
            }
        }
        finally
        {
            try { if (proc != null && !proc.HasExited) { proc.Kill(entireProcessTree: true); } } catch { /* ignore */ }
            proc?.Dispose();
            (textures as IDisposable)?.Dispose();
            (visualizers as IDisposable)?.Dispose();
        }
    }

    private async Task ConcatAndMuxAsync(Options o, IReadOnlyList<string> chunkPaths, string outPath, double duration, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var listPath = Path.Combine(Path.GetDirectoryName(chunkPaths[0])!, "concat.txt");
        await File.WriteAllTextAsync(listPath, string.Join("\n", System.Linq.Enumerable.Select(chunkPaths, p => $"file '{p.Replace("'", "'\\''")}'")), ct).ConfigureAwait(false);

        var hasAudio = !string.IsNullOrWhiteSpace(o.AudioPath) && File.Exists(o.AudioPath);
        var args = new System.Text.StringBuilder();
        args.Append($"-y -f concat -safe 0 -i \"{listPath}\" ");
        if (hasAudio) args.Append($"-i \"{o.AudioPath}\" ");
        args.Append("-c:v copy ");
        args.Append(hasAudio ? "-c:a aac -b:a 192k -map 0:v:0 -map 1:a:0 " : "-map 0:v:0 ");
        args.Append($"-t {duration.ToString("F3", inv)} -movflags +faststart \"{outPath}\"");

        var psi = new ProcessStartInfo(o.FfmpegPath, args.ToString())
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(o.FfmpegPath) ?? ""
        };
        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg (concat).");
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                Log.Error("Compositor export concat failed (exit {Code}):\n{Err}", proc.ExitCode, stderr);
                throw new InvalidOperationException($"Export concat failed (ffmpeg exit {proc.ExitCode}).");
            }
            if (!File.Exists(outPath))
                throw new FileNotFoundException("Export produced no output file.", outPath);
            Log.Information("Compositor export complete: {Path}", outPath);
        }
        finally
        {
            // Kill before dispose so a cancelled concat doesn't leave an orphaned ffmpeg writing output.
            try { if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            proc?.Dispose();
        }
    }
}
