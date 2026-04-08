#nullable enable
#pragma warning disable CS0618 // RenderConfig.TextSegments is intentionally kept for legacy compatibility paths.
using Serilog;
using PodcastVideoEditor.Core.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private static Process? _currentRenderProcess;

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
        bool renderChunked = true)
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

        // Automatic chunking for medium/large timelines: at ~45+ visual segments,
        // the monolithic overlay graph usually becomes CPU-bound on consumer CPUs.
        if (renderChunked && config.VisualSegments != null && config.VisualSegments.Count >= 45)
        {
            Log.Information("Large timeline detected ({Count} segments) — using chunked render pipeline " +
                            "for better CPU efficiency", config.VisualSegments.Count);
            return await RenderVideoAsync_Chunked(config, progress, cancellationToken, chunkSize: 60);
        }

        return await Task.Run(async () =>
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

            var (ffmpegArgs, normalizedConfig) = FFmpegCommandComposer.Build(config);

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(normalizedConfig.OutputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            try
            {

                Log.Information("Starting render: {OutputPath}", normalizedConfig.OutputPath);
                Log.Information("FFmpeg command: ffmpeg {Args}", ffmpegArgs);
                Log.Information("Render segments: {Visual} visual, {Text} text, {Audio} audio",
                    normalizedConfig.VisualSegments?.Count ?? 0,
                    normalizedConfig.TextSegments?.Count ?? 0,
                    normalizedConfig.AudioSegments?.Count ?? 0);

                // Log GPU pipeline status for diagnostics
                var gpuCaps = FFmpegCommandComposer.GetGpuCapabilities();
                Log.Debug("GPU capabilities: {Status} | Encode: {GpuEncode}, Filter: {GpuFilter}, H264: {H264}, HEVC: {HEVC}",
                    gpuCaps.StatusText, gpuCaps.IsGpuEncoding, gpuCaps.IsGpuFiltering,
                    gpuCaps.H264Encoder, gpuCaps.HevcEncoder);

                // Execute FFmpeg
                var (success, output) = await ExecuteFFmpegAsync(_ffmpegPath!, ffmpegArgs, progress, cancellationToken);

                if (!success)
                {
                    Log.Error("FFmpeg render failed: {Output}", output);
                    throw new InvalidOperationException($"Render failed: {output}");
                }

                if (!File.Exists(normalizedConfig.OutputPath))
                    throw new FileNotFoundException("Output file was not created");

                var fileSize = new FileInfo(normalizedConfig.OutputPath).Length;
                Log.Information("Render completed: {OutputPath} ({FileSize} bytes)", normalizedConfig.OutputPath, fileSize);

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
                CleanupRenderTempFiles(normalizedConfig.OutputPath);
            }
        }, cancellationToken);
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

        // Analyze effect complexity to determine adaptive chunk window size
        var effectAnalysis = AnalyzeEffectComplexity(visualsByTime);
        var adaptiveMaxChunkDuration = DetermineAdaptiveChunkWindow(effectAnalysis);

        var chunkWindows = BuildChunkWindows(visualsByTime, timelineEndTime, maxChunkDurationSeconds: adaptiveMaxChunkDuration);
        var chunkCount = chunkWindows.Count;

        Log.Information("Chunked render: splitting {TotalSegments} visual segments into {ChunkCount} chunks " +
            "(adaptive window={AdaptiveWindow:F1}s, base=60s; effect densities: zoom={ZoomDensity:P1}, visualizer={VisualizerDensity:P1})", 
            visualSegs.Count, chunkCount, adaptiveMaxChunkDuration, 
            effectAnalysis.ZoomPanDensity, effectAnalysis.VisualizerDensity);

        var intermediateChunks = new List<string>();
        var chunkTempDir = Path.Combine(Path.GetTempPath(), "pve", $"chunks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(chunkTempDir);

        try
        {
            // Render each chunk
            for (int i = 0; i < chunkCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Render canceled");

                var window = chunkWindows[i];
                var chunkStartTime = window.StartTime;
                var chunkEndTime = window.EndTime;

                var chunkVisuals = BuildChunkVisualSegments(visualSegs, chunkStartTime, chunkEndTime);
                var chunkAudioSegments = BuildChunkAudioSegments(config, chunkStartTime, chunkEndTime);

                // Create config for this chunk
                var chunkConfig = BuildChunkConfigForWindow(
                    config,
                    Path.Combine(chunkTempDir, $"chunk_{i:D3}.mp4"),
                    chunkVisuals,
                    chunkAudioSegments);

                // Progress: (i / chunkCount * 0.9) — reserve 90-100% for concat phase
                var chunkProgress = new Progress<RenderProgress>(p =>
                {
                    var overallPct = (int)(i / (double)chunkCount * 90 + p.ProgressPercentage / (double)chunkCount * 0.9);
                    progress?.Report(new RenderProgress
                    {
                        ProgressPercentage = Math.Max(0, Math.Min(89, overallPct)),
                        Message = $"Chunk {i + 1}/{chunkCount}: {p.Message}",
                        IsComplete = false
                    });
                });

                Log.Information("Rendering chunk {ChunkNum}/{TotalChunks}: window {StartTime:F2}s..{EndTime:F2}s " +
                                "(time {StartTime:F2}s-{EndTime:F2}s), visuals={VisualCount}, audios={AudioCount}",
                    i + 1, chunkCount, chunkStartTime, chunkEndTime, chunkStartTime, chunkEndTime,
                    chunkVisuals.Count, chunkAudioSegments.Count);

                // Measure per-chunk render time for effect profiling
                var chunkTimer = System.Diagnostics.Stopwatch.StartNew();
                
                // Render this chunk (monolithic, no further splitting)
                await RenderVideoAsync(chunkConfig, chunkProgress, cancellationToken, renderChunked: false);
                chunkTimer.Stop();
                
                intermediateChunks.Add(chunkConfig.OutputPath);

                // Structured effect timing logging
                var chunkDurationSeconds = chunkEndTime - chunkStartTime;
                var chunkHasMotion = chunkVisuals.Any(v => v.MotionPreset != null && v.MotionPreset != "None");
                var effectProfile = chunkHasMotion ? "motion" : "static";

                Log.Information("EffectRenderTiming Chunk {ChunkNum}: duration={ChunkDurationSec:F2}s, elapsed={ElapsedMs:F0}ms, " +
                                "realtime={RealtimeX:F3}x, effects={EffectProfile}, visualCount={VisualCount}",
                    i + 1, chunkDurationSeconds, chunkTimer.Elapsed.TotalMilliseconds,
                    (chunkDurationSeconds * 1000) / chunkTimer.Elapsed.TotalMilliseconds,
                    effectProfile, chunkVisuals.Count);

                Log.Information("Chunk {ChunkNum} completed: {Path}", i + 1, chunkConfig.OutputPath);
            }

            // Concatenate all chunks into final output
            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 90,
                Message = "Concatenating chunks...",
                IsComplete = false
            });

            var finalPath = await ConcatenateChunksAsync(_ffmpegPath!, intermediateChunks, config.OutputPath,
                progress, cancellationToken);

            Log.Information("Chunked render completed: {Path} ({ChunkCount} chunks concatenated)",
                finalPath, chunkCount);

            return finalPath;
        }
        finally
        {
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

    private static List<(double StartTime, double EndTime)> BuildChunkWindows(
        IReadOnlyList<RenderVisualSegment> visuals,
        double timelineEndTime,
        double maxChunkDurationSeconds)
    {
        var windows = new List<(double StartTime, double EndTime)>();
        if (timelineEndTime <= 0)
            return windows;

        const double minChunkDurationSeconds = 20;
        const int targetMaxVisualsPerChunk = 24;

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

            windows.Add((startTime, endTime));

            // Guarantee forward progress even with pathological timing data.
            startTime = Math.Max(endTime, startTime + 0.001);
        }

        return windows;
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

    internal static RenderConfig BuildChunkConfigForWindow(
        RenderConfig sourceConfig,
        string outputPath,
        List<RenderVisualSegment> chunkVisuals,
        List<RenderAudioSegment> chunkAudioSegments)
    {
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
            UseGpuOverlay = true,  // Enable GPU overlay for chunked renders (simpler filter graphs)
            DisableEmbeddedTimelineSources = true
        };
    }

    private static List<RenderVisualSegment> BuildChunkVisualSegments(
        IReadOnlyList<RenderVisualSegment> allVisuals,
        double chunkStartTime,
        double chunkEndTime)
    {
        var chunkVisuals = new List<RenderVisualSegment>();
        var motionClippedAtChunkStart = 0;

        foreach (var seg in allVisuals)
        {
            if (seg.EndTime <= chunkStartTime || seg.StartTime >= chunkEndTime)
                continue;

            var clippedStart = Math.Max(seg.StartTime, chunkStartTime);
            var clippedEnd = Math.Min(seg.EndTime, chunkEndTime);
            if (clippedEnd <= clippedStart)
                continue;

            var sourceOffsetAdjustment = Math.Max(0, clippedStart - seg.StartTime);
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
                StartTime = clippedStart - chunkStartTime,
                EndTime = clippedEnd - chunkStartTime,
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
                OverlayColorHex = seg.OverlayColorHex,
                OverlayOpacity = seg.OverlayOpacity,
                TransitionType = seg.TransitionType,
                TransitionDuration = seg.TransitionDuration
            });
        }

        if (motionClippedAtChunkStart > 0)
        {
            Log.Warning(
                "Chunked render window {Start:F2}s..{End:F2}s clips {Count} image motion segments at chunk start; motion restarts inside this window.",
                chunkStartTime,
                chunkEndTime,
                motionClippedAtChunkStart);
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
                var filterScript = Path.Combine(pveDir, "fc.txt");
                if (File.Exists(filterScript))
                    File.Delete(filterScript);

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
            if (_currentRenderProcess != null && !_currentRenderProcess.HasExited)
            {
                _currentRenderProcess.Kill();
                Log.Information("Render process terminated");
            }
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
        CancellationToken cancellationToken)
    {
        // Declared outside try so catch/finally blocks can access it.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
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

            _currentRenderProcess = Process.Start(processInfo);
            if (_currentRenderProcess == null)
                return (false, "Failed to start FFmpeg process");

            // Use default scheduler behavior for render throughput.
            // Prior throttling (BelowNormal + affinity pinning) reduced export speed
            // significantly on CPU-composite workloads.
            try
            {
                _currentRenderProcess.PriorityClass = ProcessPriorityClass.Normal;
                Log.Information("FFmpeg process: Normal priority, all scheduler-managed cores (PID {Pid})",
                    _currentRenderProcess.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not set FFmpeg process priority/affinity");
            }

            // Read stdout fully in background (usually empty for FFmpeg)
            var outputTask = _currentRenderProcess.StandardOutput.ReadToEndAsync(cancellationToken);

            // Parse stderr line-by-line for progress
            var stderrLines = new System.Collections.Generic.List<string>();

            // Try to extract the explicit -t duration from the command args.
            // This is far more reliable than parsing FFmpeg's "Duration:" header lines
            // which may come from a lavfi color source or a -loop 1 image input
            // (both report unhelpful durations).
            double totalDurationSeconds = ExtractExplicitDuration(args);
            double maxDurationFromHeaders = 0;
            int inputHeaderCount = 0;

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
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal shutdown */ }
            }, heartbeatCts.Token);

            var stderrReader = _currentRenderProcess.StandardError;
            string? line;
            while ((line = await stderrReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
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
            await _currentRenderProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await outputTask.ConfigureAwait(false);

            bool success = _currentRenderProcess.ExitCode == 0;
            var fullStderr = string.Join(Environment.NewLine, stderrLines);

            var effectiveDurationSeconds = totalDurationSeconds > 0
                ? totalDurationSeconds
                : (maxDurationFromHeaders > 0 ? maxDurationFromHeaders : 0);
            var elapsedSeconds = Math.Max(0.001, encodingStopwatch.Elapsed.TotalSeconds);
            var realtimeFactor = effectiveDurationSeconds > 0
                ? effectiveDurationSeconds / elapsedSeconds
                : 0;

            Log.Information("FFmpeg finished: exit={ExitCode}, elapsed={Elapsed:F2}s, target={Target:F2}s, realtime=x{Realtime:F2}",
                _currentRenderProcess.ExitCode,
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
            _currentRenderProcess?.Dispose();
            _currentRenderProcess = null;
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
