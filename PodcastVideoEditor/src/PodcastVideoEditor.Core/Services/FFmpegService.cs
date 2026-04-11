#nullable enable
#pragma warning disable CS0618 // RenderConfig.TextSegments is intentionally kept for legacy compatibility paths.
using Serilog;
using PodcastVideoEditor.Core.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Service for FFmpeg validation and execution
/// </summary>
public static class FFmpegService
{
    private static string? _ffmpegPath;
    private static string? _ffprobePath;
    private static Version? _ffmpegVersion;
        // Tracks all concurrently running FFmpeg render processes (supports parallel chunk rendering).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Process> _activeProcesses = new();

    /// <summary>
    /// Initialize FFmpeg service and validate installation
    /// </summary>
    public static async Task<FFmpegValidationResult> InitializeAsync(string? customFFmpegPath = null)
    {
        try
        {
            // 1. Try custom path first
            if (!string.IsNullOrWhiteSpace(customFFmpegPath))
            {
                if (ValidateFFmpegPath(customFFmpegPath))
                {
                    _ffmpegPath = customFFmpegPath;
                    await DetectVersionAsync();
                    Log.Information("FFmpeg initialized with custom path: {Path}", customFFmpegPath);
                    return new FFmpegValidationResult 
                    { 
                        IsValid = true, 
                        Message = $"FFmpeg {_ffmpegVersion} found at {customFFmpegPath}",
                        FFmpegPath = customFFmpegPath,
                        Version = _ffmpegVersion
                    };
                }
                else
                {
                    Log.Warning("Custom FFmpeg path invalid: {Path}", customFFmpegPath);
                }
            }

            // 2. Prefer a bundled FFmpeg sidecar when the app was published with one.
            if (TryFindBundledLocation())
            {
                await DetectVersionAsync();
                Log.Information("FFmpeg initialized from bundled tools: {Path}", _ffmpegPath);
                return new FFmpegValidationResult
                {
                    IsValid = true,
                    Message = $"FFmpeg {_ffmpegVersion} found in bundled tools",
                    FFmpegPath = _ffmpegPath,
                    Version = _ffmpegVersion
                };
            }

            // 2.5. Use the locally-cached compat build ONLY when no bundled FFmpeg is
            //      available.  The bundled build (BtbN GPL) already has full GPU support;
            //      the compat download exists as a fallback for system-FFmpeg setups.
            if (string.IsNullOrWhiteSpace(_ffmpegPath) && FFmpegUpdateService.IsCompatBinaryPresent)
            {
                _ffmpegPath  = FFmpegUpdateService.CompatFfmpegPath;
                _ffprobePath = FFmpegUpdateService.CompatFfprobePath;
                await DetectVersionAsync();
                Log.Information("FFmpeg initialized from compat build: {Path}", _ffmpegPath);
                return new FFmpegValidationResult
                {
                    IsValid = true,
                    Message = $"FFmpeg {_ffmpegVersion} (compat build — GPU encoding enabled)",
                    FFmpegPath = _ffmpegPath,
                    Version = _ffmpegVersion
                };
            }

            // 3. Try system PATH
            if (TryFindInSystemPath())
            {
                await DetectVersionAsync();
                Log.Information("FFmpeg initialized from PATH: {Path}", _ffmpegPath);
                return new FFmpegValidationResult 
                { 
                    IsValid = true, 
                    Message = $"FFmpeg {_ffmpegVersion} found in system PATH",
                    FFmpegPath = _ffmpegPath,
                    Version = _ffmpegVersion
                };
            }

            // 4. Try common installation paths
            if (TryFindCommonLocations())
            {
                await DetectVersionAsync();
                Log.Information("FFmpeg found at common location: {Path}", _ffmpegPath);
                return new FFmpegValidationResult 
                { 
                    IsValid = true, 
                    Message = $"FFmpeg {_ffmpegVersion} found at {_ffmpegPath}",
                    FFmpegPath = _ffmpegPath,
                    Version = _ffmpegVersion
                };
            }

            var errorMsg = "FFmpeg not found. Please install FFmpeg or set custom path in settings.";
            Log.Error(errorMsg);
            return new FFmpegValidationResult 
            { 
                IsValid = false, 
                Message = errorMsg,
                Suggestions = new[] 
                { 
                    "1. Install FFmpeg: https://ffmpeg.org/download.html",
                    "2. Add FFmpeg to system PATH",
                    "3. Set custom FFmpeg path in Settings"
                }
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing FFmpeg service");
            return new FFmpegValidationResult 
            { 
                IsValid = false, 
                Message = $"Error initializing FFmpeg: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Validate FFmpeg path exists and is executable
    /// </summary>
    private static bool ValidateFFmpegPath(string path)
    {
        return File.Exists(path) && (path.EndsWith("ffmpeg.exe") || path.EndsWith("ffmpeg"));
    }

    /// <summary>
    /// Try to find FFmpeg in system PATH
    /// </summary>
    private static bool TryFindInSystemPath()
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = pathEnv.Split(Path.PathSeparator);

            foreach (var dir in paths)
            {
                var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    _ffmpegPath = ffmpegPath;
                    _ffprobePath = Path.Combine(dir, "ffprobe.exe");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error searching PATH for FFmpeg");
        }

        return false;
    }

    /// <summary>
    /// Try to find FFmpeg in the application's bundled tools directory.
    /// </summary>
    private static bool TryFindBundledLocation()
    {
        var bundledFfmpegPath = GetBundledFfmpegPath();
        if (string.IsNullOrWhiteSpace(bundledFfmpegPath) || !File.Exists(bundledFfmpegPath))
            return false;

        _ffmpegPath = bundledFfmpegPath;
        _ffprobePath = GetBundledFfprobePath();
        return true;
    }

    /// <summary>
    /// Try to find FFmpeg in common installation directories
    /// </summary>
    private static bool TryFindCommonLocations()
    {
        // Prefer the locally-cached compatible build downloaded by FFmpegUpdateService.
        // This lives in %LOCALAPPDATA%\PodcastVideoEditor\ffmpeg-compat\ and is
        // guaranteed to work with NVENC API 12.x (driver 560.x).
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var compatBin = Path.Combine(localAppData, "PodcastVideoEditor", "ffmpeg-compat",
                                     "ffmpeg-7.1-essentials_build", "bin", "ffmpeg.exe");

        var commonPaths = new[]
        {
            compatBin,                                                                   // compat 7.1 (SDK 12.x)
            @"C:\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(localAppData, "ffmpeg", "bin", "ffmpeg.exe"),
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                _ffmpegPath = path;
                _ffprobePath = Path.Combine(Path.GetDirectoryName(path) ?? "", "ffprobe.exe");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detect FFmpeg version
    /// </summary>
    private static async Task DetectVersionAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_ffmpegPath))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_ffmpegPath) ?? ""
            };

            using var process = Process.Start(psi);
            if (process == null)
                return;

            var output = await process.StandardOutput.ReadLineAsync();
            if (string.IsNullOrEmpty(output))
                return;

            // Parse version from output: "ffmpeg version 4.4.2-1.ubuntu1"
            var parts = output.Split(' ');
            var versionPart = parts.FirstOrDefault(p => p.Contains("."));
            if (versionPart != null && Version.TryParse(versionPart, out var version))
            {
                _ffmpegVersion = version;

                // Warn if version < 4.4
                if (version < new Version(4, 4))
                {
                    Log.Warning("FFmpeg version {Version} is older than recommended 4.4+", version);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not detect FFmpeg version");
        }
    }

    /// <summary>
    /// Get FFmpeg executable path
    /// </summary>
    public static string? GetFFmpegPath()
    {
        return _ffmpegPath;
    }

    /// <summary>
    /// Override the active FFmpeg/ffprobe paths at runtime. Used by
    /// <see cref="FFmpegUpdateService"/> to redirect to a compatible build
    /// after downloading it without requiring a full re-init.
    /// </summary>
    public static void OverridePath(string ffmpegPath, string? ffprobePath = null)
    {
        if (!File.Exists(ffmpegPath))
            throw new FileNotFoundException("FFmpeg not found at override path.", ffmpegPath);

        _ffmpegPath  = ffmpegPath;
        _ffprobePath = ffprobePath ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        _ffmpegVersion = null;   // will be re-detected lazily on next call to GetFFmpegVersion()
        Log.Information("FFmpegService: Path overridden to {Path}", ffmpegPath);
    }

    /// <summary>
    /// Get the FFmpeg executable path from the bundled tools directory.
    /// </summary>
    public static string? GetBundledFfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Get the ffprobe executable path from the bundled tools directory.
    /// </summary>
    public static string? GetBundledFfprobePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Get FFmpeg version
    /// </summary>
    public static Version? GetFFmpegVersion()
    {
        return _ffmpegVersion;
    }

    /// <summary>
    /// Check if FFmpeg is initialized and valid
    /// </summary>
    public static bool IsInitialized()
    {
        return !string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath);
    }

    /// <summary>
    /// Extract a single frame from a video file at the given time (seconds) and save as PNG.
    /// Used for timeline segment thumbnails. Blocks briefly; call from background or cache result.
    /// </summary>
    /// <param name="videoPath">Full path to video file</param>
    /// <param name="timeSeconds">Time position in seconds</param>
    /// <param name="outputImagePath">Full path for output PNG file</param>
    /// <returns>True if extraction succeeded and file exists</returns>
    public static bool ExtractVideoFrameToImage(string videoPath, double timeSeconds, string outputImagePath)
    {
        if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
            return false;
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return false;
        var dir = Path.GetDirectoryName(outputImagePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        try
        {
            var timeStr = timeSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            // Method 1: -ss before -i (fast seek) with GPU hardware acceleration
            // -hwaccel auto: tries D3D11VA/DXVA2/NVDEC/QuickSync depending on available GPU
            var args = $"-y -hwaccel auto -ss {timeStr} -i \"{videoPath}\" -frames:v 1 -f image2 -q:v 2 -update 1 \"{outputImagePath}\"";
            var ok = RunFfmpegCaptureFrame(args, outputImagePath);
            if (ok)
                return true;
            // Method 2: Fallback without hwaccel if GPU decode fails
            args = $"-y -ss {timeStr} -i \"{videoPath}\" -frames:v 1 -f image2 -q:v 2 -update 1 \"{outputImagePath}\"";
            ok = RunFfmpegCaptureFrame(args, outputImagePath);
            if (ok)
                return true;
            // Method 3: -ss after -i (accurate seek, slower) for problematic files
            args = $"-y -i \"{videoPath}\" -ss {timeStr} -frames:v 1 -f image2 -q:v 2 -update 1 \"{outputImagePath}\"";
            return RunFfmpegCaptureFrame(args, outputImagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ExtractVideoFrameToImage failed: {Video} @ {Time}s", videoPath, timeSeconds);
            return false;
        }
    }

    private static bool RunFfmpegCaptureFrame(string arguments, string outputImagePath)
    {
        // Log hardware acceleration status for first few calls
        if (arguments.Contains("-hwaccel"))
            Log.Debug("FFmpeg GPU decode enabled: {Args}", arguments.Substring(0, Math.Min(100, arguments.Length)));
        
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_ffmpegPath) ?? ""
            }
        };
        process.Start();
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(15));
        
        // Log hardware acceleration errors
        if (stderr.Contains("hwaccel") && !stderr.Contains("successfully"))
            Log.Warning("FFmpeg hwaccel warning: {Stderr}", stderr.Substring(0, Math.Min(200, stderr.Length)));
        
        return process.ExitCode == 0 && File.Exists(outputImagePath);
    }

    /// <summary>
    /// Try to initialize FFmpeg synchronously (find in PATH or common locations) if not yet initialized.
    /// Called when we need a video thumbnail so thumbnails work even before user opens Settings.
    /// </summary>
    public static void EnsureInitializedSync()
    {
        if (IsInitialized())
            return;
        lock (typeof(FFmpegService))
        {
            if (IsInitialized())
                return;
            if (TryFindInSystemPath() || TryFindCommonLocations())
                Log.Information("FFmpeg initialized synchronously for thumbnails: {Path}", _ffmpegPath);
        }
    }

    /// <summary>
    /// Get or create a thumbnail image path for a video at the given time. Uses a cache directory.
    /// SYNCHRONOUS - may block UI. Use GetOrCreateVideoThumbnailPathAsync when possible.
    /// </summary>
    public static string? GetOrCreateVideoThumbnailPath(string videoPath, double timeSeconds)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return null;
        var fullVideoPath = Path.GetFullPath(videoPath);
        if (!File.Exists(fullVideoPath))
            return null;
        EnsureInitializedSync();
        if (!IsInitialized())
            return null;
        var outPath = GetThumbnailCachePathFor(fullVideoPath, timeSeconds);
        if (File.Exists(outPath))
            return outPath;
        return ExtractVideoFrameToImage(fullVideoPath, timeSeconds, outPath) ? outPath : null;
    }

    /// <summary>
    /// Get or create a thumbnail image path for a video at the given time. Uses a cache directory.
    /// ASYNC version - runs FFmpeg in background thread to avoid blocking UI.
    /// </summary>
    public static async Task<string?> GetOrCreateVideoThumbnailPathAsync(
        string videoPath, 
        double timeSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return null;
        
        var fullVideoPath = Path.GetFullPath(videoPath);
        if (!File.Exists(fullVideoPath))
            return null;
        
        // Check cache first (fast, sync)
        var outPath = GetThumbnailCachePathFor(fullVideoPath, timeSeconds);
        if (File.Exists(outPath))
            return outPath;
        
        // Run extraction in background thread
        return await Task.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return null;
            
            EnsureInitializedSync();
            if (!IsInitialized())
                return null;
            
            return ExtractVideoFrameToImage(fullVideoPath, timeSeconds, outPath) ? outPath : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the cache file path for a video thumbnail at the given time (for use by fallback methods e.g. WPF MediaPlayer).
    /// </summary>
    public static string GetThumbnailCachePathFor(string videoPath, double timeSeconds)
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor", "thumbnails");
        Directory.CreateDirectory(appData);
        var fullPath = Path.GetFullPath(videoPath);
        var hash = HashString($"{fullPath}|{timeSeconds:F2}");
        return Path.Combine(appData, hash + ".png");
    }

    private static string HashString(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "")[..16];
    }

    /// <summary>
    /// Render video from audio + image.
    /// 
    /// Features:
    /// - Single-image mode: audio + static background image
    /// - Timeline mode: multiple visual/text/audio segments with timing
    /// - Automatic chunking: large timelines (220+ visual segments) are automatically
    ///   split into smaller chunks for faster CPU-friendly rendering (~40-50% speedup).
    ///   Chunking can be disabled via renderChunked=false parameter.
    /// </summary>
    public static async Task<string> RenderVideoAsync(
        RenderConfig config,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken = default,
        bool renderChunked = true,
        bool cleanupTempFiles = true,
        TimeSpan? ffmpegTimeout = null)
    {
        if (!IsInitialized())
            throw new InvalidOperationException("FFmpeg is not initialized. Call InitializeAsync() first.");

        if (config == null)
            throw new ArgumentNullException(nameof(config));

        var hasAudioSegs = config.AudioSegments != null && config.AudioSegments.Count > 0;
        var hasTimelineVisuals = config.VisualSegments != null && config.VisualSegments.Count > 0;
        var hasTimelineText   = config.TextSegments  != null && config.TextSegments.Count  > 0;
        var hasTimelineAudio  = config.AudioSegments != null && config.AudioSegments.Count > 0;
        var hasAnyTimeline    = hasTimelineVisuals || hasTimelineText || hasTimelineAudio;

        // Legacy single-image mode requires both audio and image files.
        // Timeline mode uses anullsrc as silent placeholder when no primary audio exists.
        if (!hasAnyTimeline)
        {
            if (!hasAudioSegs && (string.IsNullOrWhiteSpace(config.AudioPath) || !File.Exists(config.AudioPath)))
                throw new FileNotFoundException($"Audio file not found: {config.AudioPath}");
            if (string.IsNullOrWhiteSpace(config.ImagePath) || !File.Exists(config.ImagePath))
                throw new FileNotFoundException($"Image file not found: {config.ImagePath}");
        }

        if (string.IsNullOrWhiteSpace(config.OutputPath))
            throw new ArgumentException("Output path is required", nameof(config.OutputPath));

        // FFmpeg (MinGW builds) uses the system's ANSI code page for file paths.
        // On machines where the code page doesn't support the filename characters
        // (e.g. Vietnamese on a non-Vietnamese locale), the path gets corrupted.
        // Workaround: render to a temp ASCII-safe path, then rename afterwards.
        var finalOutputPath = config.OutputPath;
        var needsRename = ContainsNonAscii(finalOutputPath);
        if (needsRename)
        {
            var dir = Path.GetDirectoryName(finalOutputPath) ?? Path.GetTempPath();
            var ext = Path.GetExtension(finalOutputPath);
            var safeName = $"pve-render-{Guid.NewGuid():N}{ext}";
            config.OutputPath = Path.Combine(dir, safeName);
            Log.Information("Output path contains non-ASCII characters; using temp path: {TempPath}", config.OutputPath);
        }

        try
        {

        string resultPath;
        if (ShouldUseChunkedRender(config, renderChunked))
        {
            Log.Information("Chunked render enabled: using adaptive chunk pipeline for this timeline.");
            resultPath = await RenderVideoAsync_Chunked(config, progress, cancellationToken, chunkSize: 60);
        }
        else
        {
        resultPath = await Task.Run(async () =>
        {
            // Build() runs inside Task.Run so it never blocks the UI thread.
            // EnsurePreferredEncodersInitialized() (called lazily from Build) can
            // probe GPU encoders with synchronous FFmpeg sub-processes; keeping
            // that work on the thread-pool avoids UI freezes.
            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 0,
                Message = "Preparing render...",
                IsComplete = false
            });

            var renderTotalTimer = Stopwatch.StartNew();
            var buildTimer = Stopwatch.StartNew();
            var (ffmpegArgs, normalizedConfig) = FFmpegCommandComposer.Build(config);
            buildTimer.Stop();

            // Ensure output directory exists
            var ioPrepTimer = Stopwatch.StartNew();
            var outputDir = Path.GetDirectoryName(normalizedConfig.OutputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            ioPrepTimer.Stop();

            try
            {

                Log.Information("Starting render: {OutputPath}", normalizedConfig.OutputPath);
                Log.Information("FFmpeg command: ffmpeg {Args}", ffmpegArgs);
                Log.Information("Render segments: {Visual} visual, {Text} text, {Audio} audio",
                    normalizedConfig.VisualSegments?.Count ?? 0,
                    normalizedConfig.TextSegments?.Count ?? 0,
                    normalizedConfig.AudioSegments?.Count ?? 0);
                Log.Information(
                    "RenderStageTiming: buildCommand={BuildMs:F0}ms, prepareOutputDir={IoPrepMs:F0}ms",
                    buildTimer.Elapsed.TotalMilliseconds,
                    ioPrepTimer.Elapsed.TotalMilliseconds);

                // Log GPU pipeline status for diagnostics
                var gpuCaps = FFmpegCommandComposer.GetGpuCapabilities();
                Log.Debug("GPU capabilities: {Status} | Encode: {GpuEncode}, Filter: {GpuFilter}, H264: {H264}, HEVC: {HEVC}",
                    gpuCaps.StatusText, gpuCaps.IsGpuEncoding, gpuCaps.IsGpuFiltering,
                    gpuCaps.H264Encoder, gpuCaps.HevcEncoder);

                // Execute FFmpeg
                var ffmpegExecTimer = Stopwatch.StartNew();
                var (success, output) = await ExecuteFFmpegAsync(_ffmpegPath!, ffmpegArgs, progress, cancellationToken, ffmpegTimeout);
                ffmpegExecTimer.Stop();

                if (!success)
                {
                    Log.Error("FFmpeg render failed: {Output}", output);
                    throw new InvalidOperationException($"Render failed: {output}");
                }

                if (!File.Exists(normalizedConfig.OutputPath))
                    throw new FileNotFoundException("Output file was not created");

                var fileSize = new FileInfo(normalizedConfig.OutputPath).Length;
                renderTotalTimer.Stop();
                Log.Information("Render completed: {OutputPath} ({FileSize} bytes)", normalizedConfig.OutputPath, fileSize);
                Log.Information(
                    "RenderStageTiming: ffmpegExecute={ExecMs:F0}ms, totalRender={TotalMs:F0}ms",
                    ffmpegExecTimer.Elapsed.TotalMilliseconds,
                    renderTotalTimer.Elapsed.TotalMilliseconds);

                progress?.Report(new RenderProgress
                {
                    ProgressPercentage = 100,
                    Message = "Render completed!",
                    IsComplete = true
                });

                return normalizedConfig.OutputPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error rendering video");
                throw;
            }
            finally
            {
                // Clean up temp filter script and text files created during render
                if (cleanupTempFiles)
                    CleanupRenderTempFiles(normalizedConfig.OutputPath);
            }
        }, cancellationToken);
        }

        // Rename temp file to final Unicode path if needed
        if (needsRename && File.Exists(resultPath))
        {
            // Sanitize filename portion to strip characters illegal on Windows (: ? * < > | " etc.)
            var dir = Path.GetDirectoryName(finalOutputPath) ?? Path.GetTempPath();
            var rawName = Path.GetFileName(finalOutputPath);
            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(rawName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            finalOutputPath = Path.Combine(dir, safeName);

            File.Move(resultPath, finalOutputPath, overwrite: true);
            Log.Information("Renamed temp render output to final path: {FinalPath}", finalOutputPath);
            resultPath = finalOutputPath;
        }

        return resultPath;

        }
        finally
        {
            // Restore original OutputPath on config in case caller inspects it
            if (needsRename)
                config.OutputPath = finalOutputPath;
        }
    }

    /// <summary>
    /// Returns true if the path contains any character outside the ASCII range (0-127).
    /// Used to detect paths that may be corrupted by ffmpeg's ANSI code page handling on Windows.
    /// </summary>
    private static bool ContainsNonAscii(string path)
    {
        foreach (var c in path)
        {
            if (c > 127) return true;
        }
        return false;
    }

    private static bool ShouldUseChunkedRender(RenderConfig config, bool renderChunked)
    {
        if (!renderChunked)
            return false;

        var visuals = (config.VisualSegments ?? [])
            .Where(v => v.EndTime > v.StartTime)
            .ToList();

        const int hardMinimumVisuals = 45;
        if (visuals.Count < hardMinimumVisuals)
            return false;

        var timelineDuration = ComputeTimelineDuration(config, visuals);
        var peakConcurrency = EstimatePeakVisualConcurrency(visuals);

        // Avoid chunk startup/concat overhead on short medium-density projects.
        // For heavier timelines (many visuals, longer duration, or dense overlap),
        // chunking usually wins despite per-chunk FFmpeg startup cost.
        const int preferredVisualThreshold = 80;
        const double preferredDurationThreshold = 180.0;
        const int preferredConcurrencyThreshold = 6;

        var shouldChunk = visuals.Count >= preferredVisualThreshold
            || timelineDuration >= preferredDurationThreshold
            || peakConcurrency >= preferredConcurrencyThreshold;

        Log.Information(
            "Chunked render gate: visuals={Visuals}, timeline={Timeline:F2}s, peakConcurrency={Peak} => {Decision}",
            visuals.Count,
            timelineDuration,
            peakConcurrency,
            shouldChunk ? "chunked" : "monolithic");

        return shouldChunk;
    }

    private static double ComputeTimelineDuration(RenderConfig config, IReadOnlyCollection<RenderVisualSegment> visuals)
    {
        var endTimes = visuals.Select(v => v.EndTime)
            .Concat((config.TextSegments ?? []).Select(t => t.EndTime))
            .Concat((config.AudioSegments ?? []).Select(a => a.EndTime));

        return endTimes.Any() ? endTimes.Max() : 0;
    }

    private static int EstimatePeakVisualConcurrency(IReadOnlyCollection<RenderVisualSegment> visuals)
    {
        if (visuals.Count == 0)
            return 0;

        var events = new List<(double Time, int Delta)>();
        foreach (var seg in visuals)
        {
            events.Add((seg.StartTime, +1));
            events.Add((seg.EndTime, -1));
        }

        var sorted = events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Delta)
            .ToList();

        var active = 0;
        var peak = 0;
        foreach (var evt in sorted)
        {
            active += evt.Delta;
            if (active > peak)
                peak = active;
        }

        return peak;
    }

    /// <summary>
    /// Render video using chunked pipeline: split large visual timelines into smaller chunks,
    /// render each independently, then concatenate results.
    /// This reduces FFmpeg filter graph complexity from O(n) filters to O(n/chunkSize).
    /// 
    /// Example: 420 visual segments → 7 chunks × 60 segments → 7 smaller renders → concat
    /// 
    /// Benefits:
    /// - Each filter graph is simpler, CPU can evaluate faster
    /// - Intermediate renders can potentially run in parallel (future enhancement)
    /// - Overall render time: ~40-50% faster for heavy timelines (420+ segments)
    /// 
    /// Drawbacks:
    /// - More I/O (temporary chunk files)
    /// - Requires concat demuxer post-processing
    /// </summary>
    private static async Task<string> RenderVideoAsync_Chunked(
        RenderConfig config,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken,
        int chunkSize = 60)
    {
        var chunkedTotalTimer = Stopwatch.StartNew();
        var chunkPlanningTimer = Stopwatch.StartNew();
        const int minVisualSegmentsForChunking = 45;

        if (config.VisualSegments == null || config.VisualSegments.Count < minVisualSegmentsForChunking)
        {
            // Not worth chunking for small timelines; fall back to monolithic render
            Log.Information("Chunked render: timeline too small ({Count} segments, threshold={Threshold}), using monolithic pipeline",
                config.VisualSegments?.Count ?? 0, minVisualSegmentsForChunking);
            return await RenderVideoAsync(config, progress, cancellationToken, renderChunked: false);
        }

        var visualSegs = config.VisualSegments;
        var visualsByTime = visualSegs
            .Where(v => v.EndTime > v.StartTime)
            .OrderBy(v => v.StartTime)
            .ThenBy(v => v.ZOrder)
            .ToList();

        var allEndTimes = visualsByTime.Select(v => v.EndTime)
            .Concat((config.AudioSegments ?? []).Select(a => a.EndTime));
        var timelineEndTime = allEndTimes.Any() ? allEndTimes.Max() : 0;

        // Analyze effect complexity and timeline density to determine adaptive chunk windows.
        var effectAnalysis = AnalyzeEffectComplexity(visualsByTime);
        var adaptiveMaxChunkDuration = DetermineAdaptiveChunkWindow(effectAnalysis);
        var peakConcurrency = EstimatePeakVisualConcurrency(visualsByTime);
        var averageConcurrency = EstimateAverageVisualConcurrency(visualsByTime, Math.Max(0.001, timelineEndTime));
        var (targetMaxVisualsPerChunk, minChunkDurationSeconds) = DetermineChunkPlannerTargets(visualsByTime.Count, peakConcurrency);

        var chunkWindows = BuildChunkWindows(
            visualsByTime,
            timelineEndTime,
            maxChunkDurationSeconds: adaptiveMaxChunkDuration,
            targetMaxVisualsPerChunk: targetMaxVisualsPerChunk,
            minChunkDurationSeconds: minChunkDurationSeconds);
        chunkWindows = NormalizeChunkWindowsToFrameGrid(chunkWindows, timelineEndTime, config.FrameRate);
        var chunkCount = chunkWindows.Count;
        chunkPlanningTimer.Stop();

        Log.Information("Chunked render: splitting {TotalSegments} visual segments into {ChunkCount} chunks " +
            "(adaptive window={AdaptiveWindow:F1}s, min window={MinWindow:F1}s, target visuals/chunk={TargetVisuals}; effect densities: zoom={ZoomDensity:P1}, visualizer={VisualizerDensity:P1}, peakConcurrency={PeakConcurrency}, avgConcurrency={AvgConcurrency:F2})", 
            visualSegs.Count, chunkCount, adaptiveMaxChunkDuration, minChunkDurationSeconds, targetMaxVisualsPerChunk,
            effectAnalysis.ZoomPanDensity, effectAnalysis.VisualizerDensity, peakConcurrency, averageConcurrency);
        Log.Information("Chunked render planning completed in {ElapsedMs:F0}ms", chunkPlanningTimer.Elapsed.TotalMilliseconds);

        var intermediateChunks = new List<string>();
        var chunkTempDir = Path.Combine(Path.GetTempPath(), "pve", $"chunks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(chunkTempDir);

        try
        {
                // Degree of parallelism: 2 on machines with ≥12 logical cores.
                // Each parallel FFmpeg handles a separate zoompan/overlay graph on independent cores.
                var parallelDegree = Environment.ProcessorCount >= 12 ? 2 : 1;
                Log.Information("Parallel chunk rendering: degree={Degree} ({Cores} logical cores)", parallelDegree, Environment.ProcessorCount);

                var completedCount = 0;
                var totalChunksForProgress = chunkWindows.Count;
                var chunkOutputPaths = new string?[chunkWindows.Count];
                var semaphore = new System.Threading.SemaphoreSlim(parallelDegree);
                var timedOutChunkIndexes = new System.Collections.Concurrent.ConcurrentBag<int>();
                long serialEquivalentTicks = 0;
                var parallelPhaseTimer = Stopwatch.StartNew();

                // Build and start all chunk tasks. A chunk that times out is retried
                // sequentially after the parallel phase completes.
                var chunkTasks = Enumerable.Range(0, chunkWindows.Count).Select(async idx =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var window = chunkWindows[idx];
                        var chunkStartTime = window.StartTime;
                        var chunkEndTime = window.EndTime;
                        var chunkVisuals = BuildChunkVisualSegments(visualSegs, chunkStartTime, chunkEndTime, config.FrameRate);
                        var chunkAudioSegments = BuildChunkAudioSegments(config, chunkStartTime, chunkEndTime);
                        var chunkDurationSeconds = Math.Max(0.1, chunkEndTime - chunkStartTime);
                        var chunkFfmpegTimeout = EstimateChunkFfmpegTimeout(chunkDurationSeconds);
                        var chunkPeakConcurrency = EstimatePeakVisualConcurrency(chunkVisuals);
                        var chunkAverageConcurrency = EstimateAverageVisualConcurrency(chunkVisuals, chunkDurationSeconds);
                        var chunkMotionDensity = chunkVisuals.Count == 0
                            ? 0.0
                            : (double)chunkVisuals.Count(v => !string.IsNullOrWhiteSpace(v.MotionPreset) && v.MotionPreset != MotionPresets.None) / chunkVisuals.Count;
                        var chunkDiag = AnalyzeChunkDiagnostics(chunkVisuals, chunkAudioSegments);

                        var chunkConfig = BuildChunkConfigForWindow(
                            config,
                            Path.Combine(chunkTempDir, $"chunk_{idx:D3}.mp4"),
                            chunkVisuals,
                            chunkAudioSegments);

                        int localIdx = idx; // capture for lambda
                        var chunkProgress = new Progress<RenderProgress>(p =>
                        {
                            var done = Volatile.Read(ref completedCount);
                            var overallPct = (int)(done / (double)totalChunksForProgress * 90);
                            var stageMessage = p.IsComplete ? "encoded" : p.Message;
                            progress?.Report(new RenderProgress
                            {
                                ProgressPercentage = Math.Max(0, Math.Min(89, overallPct)),
                                Message = $"Chunk {localIdx + 1}/{totalChunksForProgress}: {stageMessage}",
                                IsComplete = false
                            });
                        });

                        Log.Information(
                            "Rendering chunk {ChunkNum}/{TotalChunks}: window {StartTime:F2}s..{EndTime:F2}s, visuals={VisualCount}, audios={AudioCount}, signature={ChunkSignature}, media=img:{ImageCount},vid:{VideoCount},alpha:{AlphaCount},motion:{MotionCount},fade:{FadeCount},uniqueVisualSources:{UniqueVisualSources},uniqueAudioSources:{UniqueAudioSources}",
                            idx + 1,
                            totalChunksForProgress,
                            chunkStartTime,
                            chunkEndTime,
                            chunkVisuals.Count,
                            chunkAudioSegments.Count,
                            chunkDiag.Signature,
                            chunkDiag.ImageCount,
                            chunkDiag.VideoCount,
                            chunkDiag.AlphaCount,
                            chunkDiag.MotionCount,
                            chunkDiag.FadeCount,
                            chunkDiag.UniqueVisualSourceCount,
                            chunkDiag.UniqueAudioSourceCount);

                        var chunkTimer = Stopwatch.StartNew();
                        try
                        {
                            await RenderVideoAsync(
                                chunkConfig,
                                chunkProgress,
                                cancellationToken,
                                renderChunked: false,
                                cleanupTempFiles: false,
                                ffmpegTimeout: chunkFfmpegTimeout);
                        }
                        catch (InvalidOperationException ex) when (IsRecoverableChunkRenderError(ex))
                        {
                            chunkTimer.Stop();
                            timedOutChunkIndexes.Add(idx);
                            var recoverReason = GetRecoverableChunkErrorReason(ex);
                            Log.Warning(
                                ex,
                                "Chunk {ChunkNum}/{TotalChunks} hit recoverable FFmpeg startup failure ({Reason}) after {TimeoutSeconds:F0}s timeout budget; scheduling sequential split fallback (signature={ChunkSignature}).",
                                idx + 1,
                                totalChunksForProgress,
                                recoverReason,
                                chunkFfmpegTimeout.TotalSeconds,
                                chunkDiag.Signature);
                            return;
                        }
                        chunkTimer.Stop();
                        Interlocked.Add(ref serialEquivalentTicks, chunkTimer.ElapsedTicks);

                        chunkOutputPaths[idx] = chunkConfig.OutputPath;
                        var done2 = Interlocked.Increment(ref completedCount);

                        var chunkHasMotion = chunkVisuals.Any(v => v.MotionPreset != null && v.MotionPreset != "None");
                        Log.Information("EffectRenderTiming Chunk {ChunkNum}: duration={Duration:F2}s, elapsed={ElapsedMs:F0}ms, " +
                            "realtime={Rt:F3}x, effects={Effects}, visualCount={VisCount}, peakConcurrency={PeakConcurrency}, avgConcurrency={AvgConcurrency:F2}, motionDensity={MotionDensity:P1}",
                            idx + 1, chunkDurationSeconds, chunkTimer.Elapsed.TotalMilliseconds,
                            chunkDurationSeconds * 1000 / chunkTimer.Elapsed.TotalMilliseconds,
                            chunkHasMotion ? "motion" : "static", chunkVisuals.Count,
                            chunkPeakConcurrency, chunkAverageConcurrency, chunkMotionDensity);
                        Log.Information("Chunk {ChunkNum} completed: {Path}", idx + 1, chunkConfig.OutputPath);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(chunkTasks).ConfigureAwait(false);

                var timedOutChunks = timedOutChunkIndexes
                    .Distinct()
                    .OrderBy(i => i)
                    .ToList();

                if (timedOutChunks.Count > 0)
                {
                    Log.Warning(
                        "Sequential split fallback activated for {Count} recoverable failed chunk(s): {Chunks}",
                        timedOutChunks.Count,
                        string.Join(",", timedOutChunks.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture))));

                    foreach (var idx in timedOutChunks)
                    {
                        var fallbackTimer = Stopwatch.StartNew();
                        var recoveredPath = await RenderChunkWithSplitFallbackAsync(
                            config,
                            visualSegs,
                            chunkWindows[idx],
                            idx,
                            chunkTempDir,
                            cancellationToken).ConfigureAwait(false);
                        fallbackTimer.Stop();
                        Interlocked.Add(ref serialEquivalentTicks, fallbackTimer.ElapsedTicks);

                        chunkOutputPaths[idx] = recoveredPath;
                        Interlocked.Increment(ref completedCount);

                        var done = Volatile.Read(ref completedCount);
                        var overallPct = (int)(done / (double)totalChunksForProgress * 90);
                        progress?.Report(new RenderProgress
                        {
                            ProgressPercentage = Math.Max(0, Math.Min(89, overallPct)),
                            Message = $"Chunk {idx + 1}/{totalChunksForProgress}: recovered via split fallback",
                            IsComplete = false
                        });
                    }
                }

                parallelPhaseTimer.Stop();

                var serialEquivalent = TimeSpan.FromTicks(Volatile.Read(ref serialEquivalentTicks));
                var wallMs = parallelPhaseTimer.Elapsed.TotalMilliseconds;
                var serialMs = serialEquivalent.TotalMilliseconds;
                var speedup = wallMs > 1.0 ? serialMs / wallMs : 1.0;
                var efficiency = parallelDegree > 0 ? speedup / parallelDegree : 1.0;
                Log.Information(
                    "ParallelChunkEfficiency: degree={Degree}, chunkCount={ChunkCount}, serialEquivalentMs={SerialMs:F0}, wallMs={WallMs:F0}, speedup={Speedup:F2}x, efficiency={Efficiency:P0}",
                    parallelDegree,
                    totalChunksForProgress,
                    serialMs,
                    wallMs,
                    speedup,
                    efficiency);

                // Collect results in order
                for (int i = 0; i < chunkOutputPaths.Length; i++)
                {
                    var path = chunkOutputPaths[i];
                    if (path == null)
                        throw new InvalidOperationException($"Chunk {i + 1} did not produce an output file");
                    intermediateChunks.Add(path);
                }

            // Concatenate all chunks into final output
            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 90,
                Message = "Concatenating chunks...",
                IsComplete = false
            });

            var concatTimer = Stopwatch.StartNew();
            var finalPath = await ConcatenateChunksAsync(_ffmpegPath!, intermediateChunks, config.OutputPath,
                progress, cancellationToken);
            concatTimer.Stop();
            chunkedTotalTimer.Stop();

            Log.Information("Chunked render completed: {Path} ({ChunkCount} chunks concatenated)",
                finalPath, chunkCount);
            Log.Information(
                "ChunkedRenderTiming: concat={ConcatMs:F0}ms, total={TotalMs:F0}ms",
                concatTimer.Elapsed.TotalMilliseconds,
                chunkedTotalTimer.Elapsed.TotalMilliseconds);

            return finalPath;
        }
        finally
        {
            // Chunk renders share the same rasterized text temp directory.
            // Clean once after all chunks complete to avoid deleting text inputs
            // needed by later chunks.
            CleanupRenderTempFiles(config.OutputPath);

            // Clean up intermediate chunks
            try
            {
                if (Directory.Exists(chunkTempDir))
                    Directory.Delete(chunkTempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not clean up chunk temp directory");
            }
        }
    }

    private static bool IsRecoverableChunkRenderError(InvalidOperationException ex)
    {
        if (ex == null)
            return false;

        var message = ex.Message ?? string.Empty;
        return message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("startup stalled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("opening inputs", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRecoverableChunkErrorReason(InvalidOperationException ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("startup stalled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("opening inputs", StringComparison.OrdinalIgnoreCase))
        {
            return "startup-stall";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";

        return "unknown";
    }

    internal static bool TrySplitChunkWindow(double startTime, double endTime, out (double Start, double End) left, out (double Start, double End) right)
    {
        left = default;
        right = default;

        var duration = endTime - startTime;
        if (duration < 6.0)
            return false;

        var midpoint = startTime + duration / 2.0;
        left = (startTime, midpoint);
        right = (midpoint, endTime);
        return true;
    }

    private static async Task<string> RenderChunkWithSplitFallbackAsync(
        RenderConfig sourceConfig,
        IReadOnlyList<RenderVisualSegment> allVisuals,
        (double StartTime, double EndTime) window,
        int chunkIndex,
        string chunkTempDir,
        CancellationToken cancellationToken)
    {
        var targetOutputPath = Path.Combine(chunkTempDir, $"chunk_{chunkIndex:D3}.mp4");
        var duration = Math.Max(0.1, window.EndTime - window.StartTime);

        // If the timed-out window is already tiny, retry once with a higher timeout.
        if (!TrySplitChunkWindow(window.StartTime, window.EndTime, out var leftWindow, out var rightWindow))
        {
            var directVisuals = BuildChunkVisualSegments(allVisuals, window.StartTime, window.EndTime, sourceConfig.FrameRate);
            var directAudios = BuildChunkAudioSegments(sourceConfig, window.StartTime, window.EndTime);
            var directConfig = BuildChunkConfigForWindow(sourceConfig, targetOutputPath, directVisuals, directAudios);
            var directTimeout = TimeSpan.FromSeconds(Math.Clamp(duration * 18.0, 240.0, 1200.0));

            Log.Warning(
                "Chunk fallback direct retry (unsplittable window {Start:F2}s..{End:F2}s, timeout={Timeout:F0}s)",
                window.StartTime,
                window.EndTime,
                directTimeout.TotalSeconds);

            await RenderVideoAsync(
                directConfig,
                progress: null,
                cancellationToken,
                renderChunked: false,
                cleanupTempFiles: false,
                ffmpegTimeout: directTimeout).ConfigureAwait(false);

            return directConfig.OutputPath;
        }

        var splitParts = new[] { leftWindow, rightWindow };
        var splitOutputs = new List<string>(2);

        for (var part = 0; part < splitParts.Length; part++)
        {
            var partWindow = splitParts[part];
            var partDuration = Math.Max(0.1, partWindow.End - partWindow.Start);
            var partVisuals = BuildChunkVisualSegments(allVisuals, partWindow.Start, partWindow.End, sourceConfig.FrameRate);
            var partAudios = BuildChunkAudioSegments(sourceConfig, partWindow.Start, partWindow.End);
            var partDiag = AnalyzeChunkDiagnostics(partVisuals, partAudios);
            var partOutputPath = Path.Combine(chunkTempDir, $"chunk_{chunkIndex:D3}_retry_{part}.mp4");
            var partConfig = BuildChunkConfigForWindow(sourceConfig, partOutputPath, partVisuals, partAudios);
            var partTimeout = TimeSpan.FromSeconds(Math.Clamp(partDuration * 18.0, 180.0, 900.0));

            Log.Warning(
                "Chunk fallback split render: chunk {ChunkNum} part {PartNum}/2 window {Start:F2}s..{End:F2}s, timeout={Timeout:F0}s, signature={ChunkSignature}, media=img:{ImageCount},vid:{VideoCount},alpha:{AlphaCount},motion:{MotionCount},fade:{FadeCount}",
                chunkIndex + 1,
                part + 1,
                partWindow.Start,
                partWindow.End,
                partTimeout.TotalSeconds,
                partDiag.Signature,
                partDiag.ImageCount,
                partDiag.VideoCount,
                partDiag.AlphaCount,
                partDiag.MotionCount,
                partDiag.FadeCount);

            await RenderVideoAsync(
                partConfig,
                progress: null,
                cancellationToken,
                renderChunked: false,
                cleanupTempFiles: false,
                ffmpegTimeout: partTimeout).ConfigureAwait(false);

            splitOutputs.Add(partConfig.OutputPath);
        }

        Log.Warning(
            "Chunk fallback concat: chunk {ChunkNum} from {PartCount} split part(s)",
            chunkIndex + 1,
            splitOutputs.Count);

        await ConcatenateChunksAsync(_ffmpegPath!, splitOutputs, targetOutputPath, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return targetOutputPath;
    }

    private static TimeSpan EstimateChunkFfmpegTimeout(double chunkDurationSeconds)
    {
        // Keep enough headroom for heavy chunks, but do not allow unbounded waits.
        // 12x realtime with clamps has worked well in practice for motion-heavy windows.
        var seconds = Math.Clamp(chunkDurationSeconds * 12.0, 180.0, 900.0);
        return TimeSpan.FromSeconds(seconds);
    }

    internal static List<(double StartTime, double EndTime)> BuildChunkWindows(
        IReadOnlyList<RenderVisualSegment> visuals,
        double timelineEndTime,
        double maxChunkDurationSeconds,
        int targetMaxVisualsPerChunk,
        double minChunkDurationSeconds)
    {
        var windows = new List<(double StartTime, double EndTime)>();
        if (timelineEndTime <= 0)
            return windows;

        var startTime = 0.0;
        while (startTime < timelineEndTime)
        {
            var chunkDuration = Math.Min(maxChunkDurationSeconds, timelineEndTime - startTime);
            var endTime = Math.Min(timelineEndTime, startTime + chunkDuration);

            var overlapCount = CountVisualsInWindow(visuals, startTime, endTime);
            while (overlapCount > targetMaxVisualsPerChunk &&
                   chunkDuration > minChunkDurationSeconds + 0.001)
            {
                chunkDuration = Math.Max(minChunkDurationSeconds, chunkDuration - 10);
                endTime = Math.Min(timelineEndTime, startTime + chunkDuration);
                overlapCount = CountVisualsInWindow(visuals, startTime, endTime);
            }

            endTime = AlignChunkEndToVisualBoundary(
                visuals,
                startTime,
                endTime,
                timelineEndTime,
                minChunkDurationSeconds);

            windows.Add((startTime, endTime));

            // Guarantee forward progress even with pathological timing data.
            startTime = Math.Max(endTime, startTime + 0.001);
        }

        return windows;
    }

    internal static List<(double StartTime, double EndTime)> NormalizeChunkWindowsToFrameGrid(
        IReadOnlyList<(double StartTime, double EndTime)> windows,
        double timelineEndTime,
        int frameRate)
    {
        if (windows.Count == 0)
            return [];

        var fps = Math.Max(1, frameRate);
        var frameDuration = 1.0 / fps;
        var normalized = new List<(double StartTime, double EndTime)>(windows.Count);
        var currentStart = 0.0;

        for (var i = 0; i < windows.Count; i++)
        {
            var isLast = i == windows.Count - 1;
            var rawEnd = isLast ? timelineEndTime : windows[i].EndTime;
            var snappedEnd = isLast ? timelineEndTime : Math.Round(rawEnd * fps) / fps;

            if (snappedEnd <= currentStart + 0.000001)
                snappedEnd = Math.Min(timelineEndTime, currentStart + frameDuration);

            if (snappedEnd > timelineEndTime)
                snappedEnd = timelineEndTime;

            if (!isLast && Math.Abs(snappedEnd - rawEnd) >= 0.0005)
            {
                Log.Information(
                    "Chunk frame-grid normalization: boundary {OriginalEnd:F4}s -> {SnappedEnd:F4}s at {Fps}fps",
                    rawEnd,
                    snappedEnd,
                    fps);
            }

            normalized.Add((currentStart, snappedEnd));
            currentStart = snappedEnd;
        }

        return normalized;
    }

    internal static double AlignChunkEndToVisualBoundary(
        IReadOnlyList<RenderVisualSegment> visuals,
        double chunkStartTime,
        double proposedEndTime,
        double timelineEndTime,
        double minChunkDurationSeconds)
    {
        const double epsilon = 0.0001;
        const double maxForwardExtensionSeconds = 4.0;

        var adjustedEndTime = proposedEndTime;
        for (var iteration = 0; iteration < visuals.Count; iteration++)
        {
            var overlappingSensitiveSegment = visuals
                .Where(seg => IsChunkBoundarySensitive(seg))
                .FirstOrDefault(seg => seg.StartTime < adjustedEndTime - epsilon && seg.EndTime > adjustedEndTime + epsilon);

            if (overlappingSensitiveSegment == null)
                break;

            var snapBackEndTime = overlappingSensitiveSegment.StartTime;
            var snapForwardEndTime = Math.Min(timelineEndTime, overlappingSensitiveSegment.EndTime);
            var canSnapBack = snapBackEndTime - chunkStartTime >= minChunkDurationSeconds - epsilon;
            var forwardExtension = snapForwardEndTime - adjustedEndTime;
            var canSnapForward = forwardExtension <= maxForwardExtensionSeconds;

            if (!canSnapBack && !canSnapForward)
                break;

            if (canSnapBack && (!canSnapForward || adjustedEndTime - snapBackEndTime <= forwardExtension))
            {
                Log.Information(
                    "Chunk planner adjusted boundary {OriginalEnd:F2}s -> {AdjustedEnd:F2}s for {Source} ({SegStart:F2}s..{SegEnd:F2}s, reason=snap-back, sensitiveMotion={HasMotion}, sensitiveFade={HasFade})",
                    adjustedEndTime,
                    snapBackEndTime,
                    Path.GetFileName(overlappingSensitiveSegment.SourcePath),
                    overlappingSensitiveSegment.StartTime,
                    overlappingSensitiveSegment.EndTime,
                    !string.IsNullOrWhiteSpace(overlappingSensitiveSegment.MotionPreset) && overlappingSensitiveSegment.MotionPreset != MotionPresets.None,
                    string.Equals(overlappingSensitiveSegment.TransitionType, "fade", StringComparison.OrdinalIgnoreCase) && overlappingSensitiveSegment.TransitionDuration > 0);
                adjustedEndTime = snapBackEndTime;
                continue;
            }

            Log.Information(
                "Chunk planner adjusted boundary {OriginalEnd:F2}s -> {AdjustedEnd:F2}s for {Source} ({SegStart:F2}s..{SegEnd:F2}s, reason=snap-forward, sensitiveMotion={HasMotion}, sensitiveFade={HasFade})",
                adjustedEndTime,
                snapForwardEndTime,
                Path.GetFileName(overlappingSensitiveSegment.SourcePath),
                overlappingSensitiveSegment.StartTime,
                overlappingSensitiveSegment.EndTime,
                !string.IsNullOrWhiteSpace(overlappingSensitiveSegment.MotionPreset) && overlappingSensitiveSegment.MotionPreset != MotionPresets.None,
                string.Equals(overlappingSensitiveSegment.TransitionType, "fade", StringComparison.OrdinalIgnoreCase) && overlappingSensitiveSegment.TransitionDuration > 0);
            adjustedEndTime = snapForwardEndTime;
        }

        return Math.Max(chunkStartTime + 0.001, adjustedEndTime);
    }

    internal static bool IsChunkBoundarySensitive(RenderVisualSegment seg)
    {
        var hasFade = string.Equals(seg.TransitionType, "fade", StringComparison.OrdinalIgnoreCase)
            && seg.TransitionDuration > 0
            && seg.TransitionDuration <= seg.Duration / 2.0;
        var hasImageMotion = !seg.IsVideo
            && !string.IsNullOrWhiteSpace(seg.MotionPreset)
            && seg.MotionPreset != MotionPresets.None;

        return hasFade || hasImageMotion;
    }

    private static (int TargetMaxVisualsPerChunk, double MinChunkDurationSeconds) DetermineChunkPlannerTargets(
        int totalVisualSegments,
        int peakConcurrency)
    {
        // Dense timelines benefit from smaller chunk windows because FFmpeg overlay
        // cost scales with the number of segment chains in the filter graph.
        // Avoid over-fragmenting medium-concurrency projects into too many 12s chunks,
        // which increases startup/concat overhead and motion-boundary restarts.
        if (totalVisualSegments >= 140 || peakConcurrency >= 7)
            return (16, 12.0);

        if (totalVisualSegments >= 100 || peakConcurrency >= 6)
            return (20, 16.0);

        return (24, 20.0);
    }

    /// <summary>
    /// Analyze effect complexity for adaptive chunk sizing.
    /// Returns metrics for zoom/pan/visualizer density to inform chunking strategy.
    /// </summary>
    private static (double ZoomPanDensity, double VisualizerDensity, bool HasHighComplexity) AnalyzeEffectComplexity(
        IReadOnlyList<RenderVisualSegment> visuals)
    {
        if (visuals.Count == 0)
            return (0, 0, false);

        int zoomPanCount = 0;
        int totalCount = visuals.Count;

        foreach (var seg in visuals)
        {
            // Detect zoom/pan motion presets
            if (seg.MotionPreset != null && seg.MotionPreset != "None")
            {
                zoomPanCount++;
            }
        }

        var zoomPanDensity = (double)zoomPanCount / totalCount;
        var hasHighComplexity = zoomPanDensity > 0.6;

        return (zoomPanDensity, 0.0, hasHighComplexity);
    }

    /// <summary>
    /// Determine adaptive chunk window based on effect complexity analysis.
    /// - Low complexity: 90-120s (larger chunks = less overhead)
    /// - Medium complexity: 60s (balanced)
    /// - High complexity: 30-45s (smaller chunks = better GPU cache utilization)
    /// </summary>
    private static double DetermineAdaptiveChunkWindow(
        (double ZoomPanDensity, double VisualizerDensity, bool HasHighComplexity) analysis)
    {
        var (zoomPanDensity, vizDensity, hasHighComplexity) = analysis;

        if (hasHighComplexity)
        {
            // High complexity: zoom+pan+visualizer heavy → smaller chunks
            // Target: 30-45s to keep GPU filter graph cache warm
            return 40.0;
        }

        if (zoomPanDensity > 0.3 || vizDensity > 0.2)
        {
            // Medium complexity: some motion/visualizer → balanced chunk
            return 60.0;
        }

        // Low complexity: mostly static images → larger chunks (less startup overhead)
        return 90.0;
    }

    private static int CountVisualsInWindow(
        IReadOnlyList<RenderVisualSegment> visuals,
        double windowStart,
        double windowEnd)
    {
        var count = 0;
        foreach (var seg in visuals)
        {
            if (seg.EndTime <= windowStart || seg.StartTime >= windowEnd)
                continue;
            count++;
        }
        return count;
    }

    private static double EstimateAverageVisualConcurrency(
        IReadOnlyList<RenderVisualSegment> visuals,
        double timelineDurationSeconds)
    {
        if (timelineDurationSeconds <= 0 || visuals.Count == 0)
            return 0;

        var events = new List<(double Time, int Delta)>(visuals.Count * 2);
        foreach (var seg in visuals)
        {
            var start = Math.Max(0, seg.StartTime);
            var end = Math.Min(timelineDurationSeconds, seg.EndTime);
            if (end <= start)
                continue;

            events.Add((start, +1));
            events.Add((end, -1));
        }

        if (events.Count == 0)
            return 0;

        events.Sort((a, b) =>
        {
            var timeCmp = a.Time.CompareTo(b.Time);
            if (timeCmp != 0)
                return timeCmp;

            // End events first at identical timestamps to keep boundary-exclusive behavior.
            return a.Delta.CompareTo(b.Delta);
        });

        double previousTime = 0;
        double area = 0;
        var active = 0;
        var index = 0;

        while (index < events.Count)
        {
            var currentTime = events[index].Time;
            if (currentTime > previousTime)
            {
                area += active * (currentTime - previousTime);
                previousTime = currentTime;
            }

            while (index < events.Count && Math.Abs(events[index].Time - currentTime) < 0.000001)
            {
                active += events[index].Delta;
                index++;
            }
        }

        if (previousTime < timelineDurationSeconds)
        {
            area += active * (timelineDurationSeconds - previousTime);
        }

        return area / timelineDurationSeconds;
    }

    internal static RenderConfig BuildChunkConfigForWindow(
        RenderConfig sourceConfig,
        string outputPath,
        List<RenderVisualSegment> chunkVisuals,
        List<RenderAudioSegment> chunkAudioSegments)
    {
        var useGpuOverlayForChunk = ShouldEnableGpuOverlayForChunk(chunkVisuals);

        return new RenderConfig
        {
            AudioPath = sourceConfig.AudioPath,
            ImagePath = sourceConfig.ImagePath,
            OutputPath = outputPath,
            ResolutionWidth = sourceConfig.ResolutionWidth,
            ResolutionHeight = sourceConfig.ResolutionHeight,
            AspectRatio = sourceConfig.AspectRatio,
            Quality = sourceConfig.Quality,
            FrameRate = sourceConfig.FrameRate,
            VideoCodec = sourceConfig.VideoCodec,
            AudioCodec = sourceConfig.AudioCodec,
            ScaleMode = sourceConfig.ScaleMode,
            // Keep the user's configured primary audio gain in chunked renders.
            PrimaryAudioVolume = sourceConfig.PrimaryAudioVolume,
            VisualSegments = chunkVisuals,
            TextSegments = [],  // Text already rasterized in main render prep
            AudioSegments = chunkAudioSegments,
            // Avoid forcing GPU overlay for chunks that contain motion/fade/alpha/tint
            // because those paths run on CPU and can cause costly hwdownload/hwupload thrash.
            UseGpuOverlay = useGpuOverlayForChunk,
            DisableEmbeddedTimelineSources = true
        };
    }

    private static bool ShouldEnableGpuOverlayForChunk(IReadOnlyCollection<RenderVisualSegment> chunkVisuals)
    {
        if (chunkVisuals.Count == 0)
            return false;

        foreach (var seg in chunkVisuals)
        {
            var hasTint = seg.OverlayOpacity > 0 && !string.IsNullOrWhiteSpace(seg.OverlayColorHex);
            var hasFade = string.Equals(seg.TransitionType, "fade", StringComparison.OrdinalIgnoreCase)
                && seg.TransitionDuration > 0
                && seg.TransitionDuration <= seg.Duration / 2.0;
            var hasImageMotion = !seg.IsVideo
                && !string.IsNullOrWhiteSpace(seg.MotionPreset)
                && seg.MotionPreset != MotionPresets.None;

            // These features force CPU filter chains in the composer.
            if (seg.HasAlpha || hasTint || hasFade || hasImageMotion)
                return false;
        }

        return true;
    }

    internal static List<RenderVisualSegment> BuildChunkVisualSegments(
        IReadOnlyList<RenderVisualSegment> allVisuals,
        double chunkStartTime,
        double chunkEndTime,
        int frameRate)
    {
        var chunkVisuals = new List<RenderVisualSegment>();
        var motionClippedAtChunkStart = 0;
        var droppedSubFrameSegments = 0;
        const double epsilon = 0.0001;
        var minRenderableDuration = 0.95 / Math.Max(1, frameRate);

        foreach (var seg in allVisuals)
        {
            if (seg.EndTime <= chunkStartTime || seg.StartTime >= chunkEndTime)
                continue;

            var clippedStart = Math.Max(seg.StartTime, chunkStartTime);
            var clippedEnd = Math.Min(seg.EndTime, chunkEndTime);
            if (clippedEnd <= clippedStart)
                continue;

            var localStart = clippedStart - chunkStartTime;
            if (localStart >= 0 && localStart < 0.0005)
                localStart = 0;

            var localEnd = clippedEnd - chunkStartTime;
            if (localEnd <= localStart + 0.000001)
                continue;

            var localDuration = localEnd - localStart;
            if (localDuration < minRenderableDuration)
            {
                droppedSubFrameSegments++;
                continue;
            }

            var sourceOffsetAdjustment = Math.Max(0, clippedStart - seg.StartTime);
            var wasClippedAtStart = clippedStart > seg.StartTime + epsilon;
            var wasClippedAtEnd = clippedEnd < seg.EndTime - epsilon;
            var motionReferenceDuration = seg.MotionReferenceDurationSeconds > epsilon
                ? Math.Max(seg.MotionReferenceDurationSeconds, seg.Duration)
                : seg.Duration;
            var motionReferenceOffset = Math.Max(0, seg.MotionReferenceOffsetSeconds + sourceOffsetAdjustment);
            var hasImageMotion = !seg.IsVideo &&
                !string.IsNullOrWhiteSpace(seg.MotionPreset) &&
                seg.MotionPreset != MotionPresets.None;
            if (!seg.IsVideo &&
                sourceOffsetAdjustment > 0.0001 &&
                !string.IsNullOrWhiteSpace(seg.MotionPreset) &&
                seg.MotionPreset != MotionPresets.None)
            {
                motionClippedAtChunkStart++;
            }

            chunkVisuals.Add(new RenderVisualSegment
            {
                SourcePath = seg.SourcePath,
                StartTime = localStart,
                EndTime = localEnd,
                IsVideo = seg.IsVideo,
                SourceOffsetSeconds = seg.SourceOffsetSeconds + sourceOffsetAdjustment,
                OverlayX = seg.OverlayX,
                OverlayY = seg.OverlayY,
                ScaleWidth = seg.ScaleWidth,
                ScaleHeight = seg.ScaleHeight,
                ScaleMode = seg.ScaleMode,
                HasAlpha = seg.HasAlpha,
                ZOrder = seg.ZOrder,
                MotionPreset = seg.MotionPreset,
                MotionIntensity = seg.MotionIntensity,
                MotionReferenceOffsetSeconds = hasImageMotion ? motionReferenceOffset : 0,
                MotionReferenceDurationSeconds = hasImageMotion ? motionReferenceDuration : 0,
                OverlayColorHex = seg.OverlayColorHex,
                OverlayOpacity = seg.OverlayOpacity,
                // Prevent duplicated transition fades when a source segment is split across chunks.
                TransitionType = (wasClippedAtStart || wasClippedAtEnd) ? "none" : seg.TransitionType,
                TransitionDuration = (wasClippedAtStart || wasClippedAtEnd) ? 0 : seg.TransitionDuration
            });
        }

        if (motionClippedAtChunkStart > 0)
        {
            Log.Information(
                "Chunked render window {Start:F2}s..{End:F2}s clips {Count} image motion segments at chunk start; motion continuity is preserved with reference offsets.",
                chunkStartTime,
                chunkEndTime,
                motionClippedAtChunkStart);
        }

        if (droppedSubFrameSegments > 0)
        {
            Log.Information(
                "Chunked render window {Start:F2}s..{End:F2}s skipped {Count} sub-frame visual segment(s) to avoid startup stalls.",
                chunkStartTime,
                chunkEndTime,
                droppedSubFrameSegments);
        }

        return chunkVisuals
            .OrderBy(v => v.ZOrder)
            .ThenBy(v => v.StartTime)
            .ToList();
    }

    private static List<RenderAudioSegment> BuildChunkAudioSegments(
        RenderConfig config,
        double chunkStartTime,
        double chunkEndTime)
    {
        var chunkDuration = Math.Max(0.001, chunkEndTime - chunkStartTime);
        var chunkAudios = new List<RenderAudioSegment>();

        foreach (var seg in config.AudioSegments ?? [])
        {
            if (seg.EndTime <= chunkStartTime || seg.StartTime >= chunkEndTime)
                continue;

            var clippedStart = Math.Max(seg.StartTime, chunkStartTime);
            var clippedEnd = Math.Min(seg.EndTime, chunkEndTime);
            if (clippedEnd <= clippedStart)
                continue;

            var sourceOffsetAdjustment = Math.Max(0, clippedStart - seg.StartTime);
            var wasClippedAtStart = clippedStart > seg.StartTime + 0.0001;
            var wasClippedAtEnd = clippedEnd < seg.EndTime - 0.0001;

            chunkAudios.Add(new RenderAudioSegment
            {
                SourcePath = seg.SourcePath,
                StartTime = clippedStart - chunkStartTime,
                EndTime = clippedEnd - chunkStartTime,
                Volume = seg.Volume,
                SourceOffsetSeconds = seg.SourceOffsetSeconds + sourceOffsetAdjustment,
                // Avoid invalid/abrupt partial fades on boundary-clipped clips.
                FadeInDuration = wasClippedAtStart ? 0 : seg.FadeInDuration,
                FadeOutDuration = wasClippedAtEnd ? 0 : seg.FadeOutDuration,
                IsLooping = seg.IsLooping
            });
        }

        return chunkAudios.OrderBy(a => a.StartTime).ToList();
    }

    /// <summary>
    /// Concatenate multiple MP4 files into a single output using FFmpeg's concat demuxer.
    /// </summary>
    private static async Task<string> ConcatenateChunksAsync(
        string ffmpegPath,
        List<string> chunkPaths,
        string outputPath,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (chunkPaths.Count == 0)
            throw new ArgumentException("No chunks to concatenate", nameof(chunkPaths));

        if (chunkPaths.Count == 1)
        {
            // Single chunk — just move it to output
            File.Copy(chunkPaths[0], outputPath, overwrite: true);
            return outputPath;
        }

        var concatScriptPath = Path.Combine(Path.GetTempPath(), "pve", $"concat-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(concatScriptPath)!);

        try
        {
            // Create concat demuxer script
            var concatScript = string.Join(Environment.NewLine, 
                chunkPaths.Select(p => $"file '{Path.GetFullPath(p)}'"));
            File.WriteAllText(concatScriptPath, concatScript);

            // FFmpeg concat: copy streams (no re-encode) with -c copy
            var concatArgs = $"-f concat -safe 0 -i \"{concatScriptPath}\" -c copy -y \"{outputPath}\"";

            Log.Information("FFmpeg concat: {Args}", concatArgs);

            var (success, output) = await ExecuteFFmpegAsync(ffmpegPath, concatArgs, progress, cancellationToken);

            if (!success)
            {
                Log.Error("FFmpeg concat failed: {Output}", output);
                throw new InvalidOperationException($"Concat failed: {output}");
            }

            if (!File.Exists(outputPath))
                throw new FileNotFoundException("Concat output was not created");

            var fileSize = new FileInfo(outputPath).Length;
            Log.Information("Concat completed: {Path} ({Size} bytes)", outputPath, fileSize);

            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 100,
                Message = "Render completed!",
                IsComplete = true
            });

            return outputPath;
        }
        finally
        {
            try
            {
                if (File.Exists(concatScriptPath))
                    File.Delete(concatScriptPath);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Remove temporary filter script and text files created during a render.
    /// </summary>
    private static void CleanupRenderTempFiles(string outputPath)
    {
        try
        {
            // Clean short-path temp dir (pve/)
            var pveDir = Path.Combine(Path.GetTempPath(), "pve");
            if (Directory.Exists(pveDir))
            {
                // Delete filter script
                    // Filter scripts are now named fc-{outputname}.txt (one per render/chunk)
                    foreach (var fScript in Directory.EnumerateFiles(pveDir, "fc-*.txt"))
                    {
                        try { File.Delete(fScript); }
                        catch { /* best-effort */ }
                    }

                // Delete drawtext temp files
                var rtDir = Path.Combine(pveDir, "rt");
                if (Directory.Exists(rtDir))
                    Directory.Delete(rtDir, recursive: true);

                // Delete rasterized text PNG images (unique per-render dirs: ri*)
                foreach (var dir in Directory.EnumerateDirectories(pveDir, "ri*"))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* still locked by FFmpeg — will be cleaned on next render */ }
                }

                foreach (var dir in Directory.EnumerateDirectories(pveDir, "rs-*"))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* staging dir may still be locked or in use; retry on next cleanup */ }
                }
            }

            // Legacy path cleanup (from older versions)
            var legacyDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor");
            if (Directory.Exists(legacyDir))
            {
                try { Directory.Delete(legacyDir, recursive: true); }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clean up render temp files");
        }
    }

    /// <summary>
    /// Cancel ongoing render.
    /// </summary>
    public static void CancelRender()
    {
        try
        {
                var killed = 0;
                foreach (var (_, proc) in _activeProcesses)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            killed++;
                        }
                    }
                    catch { /* best-effort per-process */ }
                }
                if (killed > 0)
                    Log.Information("Render canceled: {Count} FFmpeg process(es) terminated", killed);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error cancelling render");
        }
    }

    /// <summary>
    /// Execute FFmpeg command and report real-time progress by parsing FFmpeg's stderr output.
    /// Progress is calculated from the "time=HH:MM:SS.ms" lines FFmpeg writes during encoding.
    /// </summary>
    private static async Task<(bool success, string output)> ExecuteFFmpegAsync(
        string ffmpegPath,
        string args,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? ffmpegTimeout = null)
    {
        // Declared outside try so catch/finally blocks can access it.
        using var timeoutCts = ffmpegTimeout.HasValue ? new CancellationTokenSource(ffmpegTimeout.Value) : null;
        using var executionCts = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(executionCts.Token);
        Task? heartbeatTask = null;
                var procKey = Guid.NewGuid();
                Process? proc = null;
        try
        {
            // Windows CreateProcess limit: 32,767 chars for the full command line.
            // Log the length and fail early with a clear message if exceeded.
            var totalCmdLen = ffmpegPath.Length + 3 + args.Length; // "path" + space + args
            Log.Information("FFmpeg command line length: {Len} chars", totalCmdLen);
            if (totalCmdLen > 32_000)
            {
                Log.Error("FFmpeg command line too long ({Len} chars, limit ~32,767). " +
                          "Reduce the number of timeline segments or use shorter file paths.", totalCmdLen);
                return (false, $"Render failed: FFmpeg command line is too long ({totalCmdLen} chars). " +
                               "Try reducing the number of text/visual segments on the timeline.");
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
            };

            proc = Process.Start(processInfo);
            if (proc == null)
                return (false, "Failed to start FFmpeg process");
            _activeProcesses[procKey] = proc;

            // Use default scheduler behavior for render throughput.
            // Prior throttling (BelowNormal + affinity pinning) reduced export speed
            // significantly on CPU-composite workloads.
            try
            {
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                    Log.Information("FFmpeg process: Normal priority, all scheduler-managed cores (PID {Pid})",
                        proc.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not set FFmpeg process priority/affinity");
            }

            // Read stdout fully in background (usually empty for FFmpeg)
                var outputTask = proc.StandardOutput.ReadToEndAsync(executionCts.Token);

            // Parse stderr line-by-line for progress
            var stderrLines = new System.Collections.Generic.List<string>();

            // Try to extract the explicit -t duration from the command args.
            // This is far more reliable than parsing FFmpeg's "Duration:" header lines
            // which may come from a lavfi color source or a -loop 1 image input
            // (both report unhelpful durations).
            double totalDurationSeconds = ExtractExplicitDuration(args);
            var inputOpenTimeoutSeconds = EstimateInputOpenTimeoutSeconds(args, totalDurationSeconds);
            var inputCount = CountInputOccurrences(args);
            double maxDurationFromHeaders = 0;
            int inputHeaderCount = 0;
            int startupStallFlag = 0;
            int startupStallElapsedSeconds = 0;

            // Track encoding phase vs finalization (faststart) phase
            bool encodingStarted = false;
            bool firstFrameReceived = false;
            var encodingStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Heartbeat task: fires every second while FFmpeg has printed "Press [q]"
            // but no frame= line has appeared yet (GPU encoder init / movie= input
            // probing can pin FFmpeg silent for 10-60s on complex timelines).
            heartbeatTask = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, heartbeatCts.Token).ConfigureAwait(false);
                        if (encodingStarted && !firstFrameReceived)
                        {
                            var elapsed = (int)encodingStopwatch.Elapsed.TotalSeconds;
                            progress?.Report(new RenderProgress
                            {
                                ProgressPercentage = 1,
                                Message = $"Opening inputs… {elapsed}s (large projects may take up to 60s)",
                                IsComplete = false
                            });

                            if (elapsed >= inputOpenTimeoutSeconds)
                            {
                                Interlocked.Exchange(ref startupStallFlag, 1);
                                Interlocked.Exchange(ref startupStallElapsedSeconds, elapsed);

                                Log.Warning(
                                    "FFmpeg startup stall watchdog: no frame after {Elapsed}s (threshold={Threshold}s, inputs={Inputs}) — terminating process to allow fallback/retry",
                                    elapsed,
                                    inputOpenTimeoutSeconds,
                                    inputCount);

                                try
                                {
                                    if (proc != null && !proc.HasExited)
                                    {
                                        proc.Kill(entireProcessTree: true);
                                        proc.WaitForExit(5000);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning(ex, "Could not terminate FFmpeg process after startup stall watchdog fired");
                                }

                                heartbeatCts.Cancel();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal shutdown */ }
            }, heartbeatCts.Token);

            var stderrReader = proc.StandardError;
            string? line;
            while ((line = await stderrReader.ReadLineAsync(executionCts.Token).ConfigureAwait(false)) != null)
            {
                stderrLines.Add(line);

                // Detect the moment FFmpeg finishes building its filter graph and is
                // about to start encoding.  FFmpeg prints "Press [q] to stop" as the
                // very last status line before the first frame= progress line appears.
                if (!encodingStarted && line.StartsWith("Press [q]", StringComparison.Ordinal))
                {
                    encodingStarted = true;
                    encodingStopwatch.Restart(); // measure time until first frame
                    progress?.Report(new RenderProgress
                    {
                        ProgressPercentage = 1,
                        Message = "FFmpeg initialized — opening inputs...",
                        IsComplete = false
                    });
                }

                // Collect Duration: headers from all inputs to find the longest real one.
                // Skip N/A and very short durations (images report 00:00:00.04 etc.).
                if (line.Contains("Duration:") && !line.Contains("N/A"))
                {
                    inputHeaderCount++;
                    var durationMatch = System.Text.RegularExpressions.Regex.Match(
                        line, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                    if (durationMatch.Success &&
                        int.TryParse(durationMatch.Groups[1].Value, out var dh) &&
                        int.TryParse(durationMatch.Groups[2].Value, out var dm) &&
                        double.TryParse(durationMatch.Groups[3].Value,
                            System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var ds))
                    {
                        var dur = dh * 3600 + dm * 60 + ds;
                        // Only consider durations > 1s as real media (skip image inputs)
                        if (dur > 1.0 && dur > maxDurationFromHeaders)
                            maxDurationFromHeaders = dur;
                    }
                }

                // Parse progress: "frame=  42 fps= 30 ... time=00:00:01.40 ..."
                if (line.StartsWith("frame=", StringComparison.Ordinal) && line.Contains("time="))
                {
                    var timeMatch = System.Text.RegularExpressions.Regex.Match(
                        line, @"time=(\d+):(\d+):(\d+\.\d+)");
                    if (timeMatch.Success &&
                        int.TryParse(timeMatch.Groups[1].Value, out var th) &&
                        int.TryParse(timeMatch.Groups[2].Value, out var tm) &&
                        double.TryParse(timeMatch.Groups[3].Value,
                            System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var ts))
                    {
                        var encodedSeconds = th * 3600 + tm * 60 + ts;

                        // Use explicit -t duration first, then best header duration
                        var effectiveDuration = totalDurationSeconds > 0
                            ? totalDurationSeconds
                            : (maxDurationFromHeaders > 0 ? maxDurationFromHeaders : 0);

                        // Cap encoding progress at 90% to reserve 90-99% for faststart phase
                        int pct = effectiveDuration > 0
                            ? (int)Math.Min(90, encodedSeconds / effectiveDuration * 90)
                            : 50;

                        // First frame received — stop the heartbeat.
                        if (!firstFrameReceived)
                        {
                            firstFrameReceived = true;
                            heartbeatCts.Cancel();
                            Log.Information("First frame received {Elapsed:F1}s after FFmpeg init",
                                encodingStopwatch.Elapsed.TotalSeconds);
                        }

                        progress?.Report(new RenderProgress
                        {
                            ProgressPercentage = pct,
                            Message = $"Encoding... {pct}%",
                            IsComplete = false
                        });
                    }
                }
            }

            if (Volatile.Read(ref startupStallFlag) == 1)
            {
                var elapsed = Math.Max(1, Volatile.Read(ref startupStallElapsedSeconds));
                var filterScriptPath = ExtractFilterScriptPath(args);
                var filterScriptExists = !string.IsNullOrWhiteSpace(filterScriptPath) && File.Exists(filterScriptPath);
                Log.Warning(
                    "FFmpeg startup stall context: elapsed={Elapsed}s, threshold={Threshold}s, inputCount={InputCount}, durationArg={DurationArg:F3}, inputHeadersSeen={InputHeadersSeen}, encodingStarted={EncodingStarted}, filterScript={FilterScript}, filterScriptExists={FilterScriptExists}",
                    elapsed,
                    inputOpenTimeoutSeconds,
                    inputCount,
                    totalDurationSeconds,
                    inputHeaderCount,
                    encodingStarted,
                    filterScriptPath ?? "<none>",
                    filterScriptExists);
                return (false, $"FFmpeg startup stalled while opening inputs after {elapsed}s");
            }

            // Encoding phase finished (stderr closed). Now FFmpeg may be doing
            // movflags +faststart (MOOV atom relocation) which produces no output.
            heartbeatCts.Cancel();
            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 95,
                Message = "Finalizing video (faststart optimization)...",
                IsComplete = false
            });

            await heartbeatTask.ConfigureAwait(false);
            await proc.WaitForExitAsync(executionCts.Token).ConfigureAwait(false);
            var stdout = await outputTask.ConfigureAwait(false);

            bool success = proc.ExitCode == 0;
            var fullStderr = string.Join(Environment.NewLine, stderrLines);

            var effectiveDurationSeconds = totalDurationSeconds > 0
                ? totalDurationSeconds
                : (maxDurationFromHeaders > 0 ? maxDurationFromHeaders : 0);
            var elapsedSeconds = Math.Max(0.001, encodingStopwatch.Elapsed.TotalSeconds);
            var realtimeFactor = effectiveDurationSeconds > 0
                ? effectiveDurationSeconds / elapsedSeconds
                : 0;

            Log.Information("FFmpeg finished: exit={ExitCode}, elapsed={Elapsed:F2}s, target={Target:F2}s, realtime=x{Realtime:F2}",
                proc.ExitCode,
                elapsedSeconds,
                effectiveDurationSeconds,
                realtimeFactor);

            if (!success)
            {
                var (category, summary, keyLines) = AnalyzeFfmpegFailure(fullStderr);
                Log.Error("FFmpeg failure category: {Category}; summary: {Summary}", category, summary);
                foreach (var key in keyLines)
                    Log.Error("FFmpeg failure line: {Line}", key);
            }

            return (success, success ? stdout : fullStderr);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                return (false, "Render canceled");

            if (timeoutCts?.IsCancellationRequested == true)
            {
                try
                {
                        if (proc != null && !proc.HasExited)
                            proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not terminate timed-out FFmpeg process");
                }

                var timeoutSeconds = ffmpegTimeout?.TotalSeconds ?? 0;
                Log.Error("FFmpeg timed out after {TimeoutSeconds:F0}s", timeoutSeconds);
                return (false, $"FFmpeg timed out after {timeoutSeconds:F0}s");
            }

            return (false, "Render canceled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing FFmpeg");
            return (false, ex.Message);
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeatTask != null)
                await heartbeatTask.ConfigureAwait(false);
            if (proc != null)
            {
                if (!proc.HasExited)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        Log.Warning("Forced cleanup terminated lingering FFmpeg process PID {Pid}", proc.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not terminate lingering FFmpeg process PID {Pid} during cleanup", proc.Id);
                    }
                }

                _activeProcesses.TryRemove(procKey, out _);
                proc.Dispose();
            }
        }
    }

    /// <summary>
    /// Extract the explicit -t (duration) value from FFmpeg command-line arguments.
    /// Returns 0 if not found.
    /// </summary>
    private static double ExtractExplicitDuration(string args)
    {
        var match = Regex.Match(args, @"-t\s+(\d+(?:\.\d+)?)");
        if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration))
        {
            return duration;
        }
        return 0;
    }

    private static int CountInputOccurrences(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return 0;

        return Regex.Matches(args, "(?:^|\\s)-i\\s+\\\"").Count;
    }

    private static int EstimateInputOpenTimeoutSeconds(string args, double explicitDurationSeconds)
    {
        var inputCount = Math.Max(1, CountInputOccurrences(args));
        var byInputCount = 20 + (inputCount * 2);
        var byDuration = explicitDurationSeconds > 0
            ? 20 + (int)Math.Ceiling(Math.Min(120.0, explicitDurationSeconds * 0.8))
            : 45;

        return Math.Clamp(Math.Max(byInputCount, byDuration), 45, 120);
    }

    private static string? ExtractFilterScriptPath(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return null;

        var match = Regex.Match(args, "-/filter_complex\\s+\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ComputeStableSignature(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "000000000000";

        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).Substring(0, 12);
    }

    private static (string Signature, int ImageCount, int VideoCount, int AlphaCount, int MotionCount, int FadeCount, int UniqueVisualSourceCount, int UniqueAudioSourceCount)
        AnalyzeChunkDiagnostics(
            IReadOnlyList<RenderVisualSegment> visuals,
            IReadOnlyList<RenderAudioSegment> audios)
    {
        var imageCount = visuals.Count(v => !v.IsVideo);
        var videoCount = visuals.Count(v => v.IsVideo);
        var alphaCount = visuals.Count(v => v.HasAlpha);
        var motionCount = visuals.Count(v => !string.IsNullOrWhiteSpace(v.MotionPreset) && v.MotionPreset != MotionPresets.None);
        var fadeCount = visuals.Count(v => string.Equals(v.TransitionType, "fade", StringComparison.OrdinalIgnoreCase) && v.TransitionDuration > 0);
        var uniqueVisualSourceCount = visuals
            .Select(v => v.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var uniqueAudioSourceCount = audios
            .Select(a => a.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var fingerprint = string.Join("|", visuals
            .OrderBy(v => v.StartTime)
            .ThenBy(v => v.ZOrder)
            .Select(v => string.Concat(
                Path.GetFileName(v.SourcePath), ":",
                v.IsVideo ? "V" : "I", ":",
                v.StartTime.ToString("F3", CultureInfo.InvariantCulture), "-",
                v.EndTime.ToString("F3", CultureInfo.InvariantCulture), ":",
                v.MotionPreset ?? string.Empty, ":",
                v.TransitionType ?? string.Empty, ":",
                v.ZOrder.ToString(CultureInfo.InvariantCulture), ":",
                v.HasAlpha ? "A1" : "A0")));
        fingerprint += "#" + string.Join("|", audios
            .OrderBy(a => a.StartTime)
            .Select(a => string.Concat(
                Path.GetFileName(a.SourcePath), ":",
                a.StartTime.ToString("F3", CultureInfo.InvariantCulture), "-",
                a.EndTime.ToString("F3", CultureInfo.InvariantCulture), ":",
                a.Volume.ToString("F2", CultureInfo.InvariantCulture))));

        var signature = ComputeStableSignature(fingerprint);
        return (signature, imageCount, videoCount, alphaCount, motionCount, fadeCount, uniqueVisualSourceCount, uniqueAudioSourceCount);
    }

    private static (string category, string summary, string[] keyLines) AnalyzeFfmpegFailure(string stderr)
    {
        var lines = stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var joined = string.Join("\n", lines).ToLowerInvariant();

        string category;
        string summary;

        if (joined.Contains("out of memory") || joined.Contains("cannot allocate memory") || joined.Contains("cuda_error_out_of_memory"))
        {
            category = "memory";
            summary = "Renderer ran out of memory (GPU or system RAM) while building/executing the FFmpeg graph.";
        }
        else if (joined.Contains("command line too long") || joined.Contains("argument list too long"))
        {
            category = "command-length";
            summary = "FFmpeg command line exceeded OS limit; timeline/filter graph must be split or script-based.";
        }
        else if (joined.Contains("unknown encoder") || joined.Contains("encoder not found"))
        {
            category = "encoder";
            summary = "Selected encoder is unavailable on current FFmpeg build or machine.";
        }
        else if (joined.Contains("no such filter") || joined.Contains("error initializing filter") || joined.Contains("error reinitializing filters"))
        {
            category = "filter-graph";
            summary = "FFmpeg filter graph is invalid or failed to initialize at runtime.";
        }
        else if (joined.Contains("permission denied") || joined.Contains("access is denied") || joined.Contains("device or resource busy"))
        {
            category = "io-permission";
            summary = "Output/input file could not be accessed due to permissions or file lock.";
        }
        else if (joined.Contains("invalid argument") || joined.Contains("option not found"))
        {
            category = "invalid-arguments";
            summary = "FFmpeg rejected one or more command arguments/options.";
        }
        else
        {
            category = "unknown";
            summary = "FFmpeg exited with an unknown error; inspect key error lines below.";
        }

        var keyLines = lines
            .Where(l => Regex.IsMatch(l, "error|failed|invalid|cannot|denied|not found|out of memory", RegexOptions.IgnoreCase))
            .TakeLast(8)
            .ToArray();

        if (keyLines.Length == 0)
            keyLines = lines.TakeLast(8).ToArray();

        return (category, summary, keyLines);
    }
}
#pragma warning restore CS0618

/// <summary>
/// FFmpeg validation result
/// </summary>
public class FFmpegValidationResult
{
    /// <summary>
    /// Is FFmpeg valid and available
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Validation message
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// FFmpeg executable path
    /// </summary>
    public string? FFmpegPath { get; set; }

    /// <summary>
    /// Detected FFmpeg version
    /// </summary>
    public Version? Version { get; set; }

    /// <summary>
    /// Suggestions for fixing issues
    /// </summary>
    public string[]? Suggestions { get; set; }
}
