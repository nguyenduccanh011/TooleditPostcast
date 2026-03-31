#nullable enable
using Serilog;
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Ensures the app always has a working GPU-encode FFmpeg regardless of which
/// system FFmpeg is installed.
///
/// Problem: gyan.dev ≥ 2025-08 builds link against NVIDIA Video Codec SDK 13.0
/// which requires driver ≥ 570.0.  Users on driver 560.x only have NVENC API 12.2
/// and fall back to libx264 (CPU).
///
/// Solution: When GPU-encoder probe fails, silently download the last gyan.dev
/// FFmpeg 7.1 release (SDK 12.x) to %LOCALAPPDATA%\PodcastVideoEditor\ffmpeg-compat\
/// and switch the path used by FFmpegService + FFmpegCommandComposer.
/// </summary>
public static class FFmpegUpdateService
{
    // ── paths ────────────────────────────────────────────────────────────
    private static readonly string _compatDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "PodcastVideoEditor", "ffmpeg-compat");

    public static string CompatFfmpegPath =>
        Path.Combine(_compatDir, "ffmpeg-7.1-essentials_build", "bin", "ffmpeg.exe");

    public static string CompatFfprobePath =>
        Path.Combine(_compatDir, "ffmpeg-7.1-essentials_build", "bin", "ffprobe.exe");

    private const string DownloadUrl =
        "https://github.com/GyanD/codexffmpeg/releases/download/7.1/ffmpeg-7.1-essentials_build.zip";

    // ── state ────────────────────────────────────────────────────────────
    private static int _downloadInProgress;   // 0 = idle, 1 = running (Interlocked flag)

    /// <summary>
    /// True when the compatible FFmpeg 7.1 binary is already present on disk.
    /// </summary>
    public static bool IsCompatBinaryPresent => File.Exists(CompatFfmpegPath);

    /// <summary>
    /// Download progress (0–100) during an active download; -1 = idle or complete.
    /// </summary>
    public static int DownloadProgressPercent { get; private set; } = -1;

    /// <summary>
    /// Fired each time <see cref="DownloadProgressPercent"/> changes.
    /// Handlers are called on a thread-pool thread – marshal to UI thread if needed.
    /// </summary>
    public static event Action<int>? DownloadProgressChanged;

    /// <summary>
    /// Fired when the download + extract succeeds and FFmpegService has been redirected.
    /// </summary>
    public static event Action? CompatBinaryReady;

    // ── public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Call once on startup (from RenderViewModel background Task).
    /// If the GPU encoder probe already worked (NVENC / QSV / AMF detected),
    /// does nothing.  Otherwise, if the compat binary is already present it
    /// redirects FFmpegService immediately; if not it triggers a background
    /// download.
    /// </summary>
    public static async Task EnsureCompatibleFFmpegAsync(CancellationToken ct = default)
    {
        // Nothing to do if GPU encoding already works.
        if (FFmpegCommandComposer.GetGpuCapabilities().IsGpuEncoding)
        {
            Log.Information("FFmpegUpdateService: GPU encoding already active – no action needed.");
            return;
        }

        // Already downloaded → redirect immediately.
        if (IsCompatBinaryPresent)
        {
            RedirectToCompatBinary();
            return;
        }

        // Trigger background download (idempotent – won't start twice).
        await DownloadAndExtractAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-check whether the compat binary is present and, if so, redirect FFmpegService.
    /// Idempotent – safe to call multiple times.
    /// </summary>
    public static void TryRedirectIfAvailable()
    {
        if (IsCompatBinaryPresent)
            RedirectToCompatBinary();
    }

    // ── internal ─────────────────────────────────────────────────────────

    /// <summary>
    /// Points FFmpegService + FFmpegCommandComposer at the compat binary and
    /// invalidates the encoder probe so the next render uses NVENC from 7.1.
    /// </summary>
    private static void RedirectToCompatBinary()
    {
        FFmpegService.OverridePath(CompatFfmpegPath, CompatFfprobePath);
        FFmpegCommandComposer.InvalidateEncoderCache();
        Log.Information("FFmpegUpdateService: Redirected to FFmpeg 7.1 at {Path}", CompatFfmpegPath);

        // Re-probe immediately (blocking is fine – we're usually on a background thread).
        var caps = FFmpegCommandComposer.GetGpuCapabilities();
        Log.Information("FFmpegUpdateService: Post-redirect GPU caps – {Status}", caps.StatusText);

        CompatBinaryReady?.Invoke();
    }

    private static async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        // Interlocked guard – only one download at a time.
        if (Interlocked.CompareExchange(ref _downloadInProgress, 1, 0) != 0)
            return;

        try
        {
            Log.Information("FFmpegUpdateService: Downloading compatible FFmpeg 7.1 from {Url}", DownloadUrl);

            Directory.CreateDirectory(_compatDir);
            var zipPath = Path.Combine(_compatDir, "ffmpeg-7.1-essentials_build.zip");

            // Download with progress.
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                // Add a user-agent so GitHub doesn't reject the request.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PodcastVideoEditor");

                using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                                 .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;

                using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var fileStream    = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer    = new byte[81920];
                long received = 0;
                int  read;

                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    if (total > 0)
                    {
                        var pct = (int)(received * 100L / total);
                        if (pct != DownloadProgressPercent)
                        {
                            DownloadProgressPercent = pct;
                            DownloadProgressChanged?.Invoke(pct);
                        }
                    }
                }
            }

            Log.Information("FFmpegUpdateService: Download complete, extracting...");
            DownloadProgressPercent = 100;
            DownloadProgressChanged?.Invoke(100);

            // Extract (ZipFile is sync-only; run on thread pool so we don't block).
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(zipPath, _compatDir, overwriteFiles: true);
            }, ct).ConfigureAwait(false);

            // Clean up the zip to save ~88 MB.
            try { File.Delete(zipPath); } catch { /* best-effort */ }

            Log.Information("FFmpegUpdateService: Extraction complete at {Dir}", _compatDir);
            DownloadProgressPercent = -1;

            if (!IsCompatBinaryPresent)
            {
                Log.Error("FFmpegUpdateService: Expected binary not found after extraction: {Path}", CompatFfmpegPath);
                return;
            }

            RedirectToCompatBinary();
        }
        catch (OperationCanceledException)
        {
            Log.Information("FFmpegUpdateService: Download cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FFmpegUpdateService: Failed to download or extract FFmpeg 7.1");
        }
        finally
        {
            DownloadProgressPercent = -1;
            Interlocked.Exchange(ref _downloadInProgress, 0);
        }
    }
}
