#nullable enable
using Serilog;
using PodcastVideoEditor.Core.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// </summary>
    public static async Task<string> RenderVideoAsync(
        RenderConfig config,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken = default)
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

        var (ffmpegArgs, normalizedConfig) = FFmpegCommandComposer.Build(config);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(normalizedConfig.OutputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        return await Task.Run(async () =>
        {
            try
            {
                progress?.Report(new RenderProgress
                {
                    ProgressPercentage = 0,
                    Message = "Preparing render...",
                    IsComplete = false
                });

                Log.Information("Starting render: {OutputPath}", normalizedConfig.OutputPath);
                Log.Information("FFmpeg command: ffmpeg {Args}", ffmpegArgs);
                Log.Information("Render segments: {Visual} visual, {Text} text, {Audio} audio",
                    normalizedConfig.VisualSegments?.Count ?? 0,
                    normalizedConfig.TextSegments?.Count ?? 0,
                    normalizedConfig.AudioSegments?.Count ?? 0);

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
    /// Remove temporary filter script and text files created during a render.
    /// </summary>
    private static void CleanupRenderTempFiles(string outputPath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor");

            // Delete filter script
            var filterScript = Path.Combine(tempDir,
                $"filter_{Path.GetFileNameWithoutExtension(outputPath)}.txt");
            if (File.Exists(filterScript))
                File.Delete(filterScript);

            // Delete text segment temp files (drawtext input)
            var textDir = Path.Combine(tempDir, "render_text");
            if (Directory.Exists(textDir))
                Directory.Delete(textDir, recursive: true);

            // Delete rasterized text PNG images (unique per-render dirs: render_text_img_*)
            foreach (var dir in Directory.EnumerateDirectories(tempDir, "render_text_img*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* still locked by FFmpeg — will be cleaned on next render */ }
            }

            // Delete baked visualizer video files
            var vizDir = Path.Combine(tempDir, "visualizer_bake");
            if (Directory.Exists(vizDir))
                Directory.Delete(vizDir, recursive: true);
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
        try
        {
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
            bool encodingFinished = false;
            var encodingStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var stderrReader = _currentRenderProcess.StandardError;
            string? line;
            while ((line = await stderrReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                stderrLines.Add(line);

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
            encodingFinished = true;
            progress?.Report(new RenderProgress
            {
                ProgressPercentage = 95,
                Message = "Finalizing video (faststart optimization)...",
                IsComplete = false
            });

            await _currentRenderProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await outputTask.ConfigureAwait(false);

            bool success = _currentRenderProcess.ExitCode == 0;
            var fullStderr = string.Join(Environment.NewLine, stderrLines);
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
        var match = System.Text.RegularExpressions.Regex.Match(args, @"-t\s+(\d+\.\d+)");
        if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration))
        {
            return duration;
        }
        return 0;
    }
}

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
