#nullable enable
using Serilog;
using PodcastVideoEditor.Core.Models;
using System;
using System.Diagnostics;
using System.IO;
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

            // 2. Try system PATH
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

            // 3. Try common installation paths
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
    /// Try to find FFmpeg in common installation directories
    /// </summary>
    private static bool TryFindCommonLocations()
    {
        var commonPaths = new[]
        {
            @"C:\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe"),
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
                CreateNoWindow = true
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
                CreateNoWindow = true
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

        if (string.IsNullOrWhiteSpace(config.AudioPath) || !File.Exists(config.AudioPath))
            throw new FileNotFoundException($"Audio file not found: {config.AudioPath}");

        if (string.IsNullOrWhiteSpace(config.ImagePath) || !File.Exists(config.ImagePath))
            throw new FileNotFoundException($"Image file not found: {config.ImagePath}");

        if (string.IsNullOrWhiteSpace(config.OutputPath))
            throw new ArgumentException("Output path is required", nameof(config.OutputPath));

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(config.OutputPath);
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

                // Build FFmpeg command
                var ffmpegArgs = BuildFFmpegCommand(config);

                Log.Information("Starting render: {OutputPath}", config.OutputPath);
                Log.Debug("FFmpeg command: ffmpeg {Args}", ffmpegArgs);

                // Execute FFmpeg
                var (success, output) = await ExecuteFFmpegAsync(_ffmpegPath!, ffmpegArgs, progress, cancellationToken);

                if (!success)
                {
                    Log.Error("FFmpeg render failed: {Output}", output);
                    throw new InvalidOperationException($"Render failed: {output}");
                }

                if (!File.Exists(config.OutputPath))
                    throw new FileNotFoundException("Output file was not created");

                var fileSize = new FileInfo(config.OutputPath).Length;
                Log.Information("Render completed: {OutputPath} ({FileSize} bytes)", config.OutputPath, fileSize);

                progress?.Report(new RenderProgress
                {
                    ProgressPercentage = 100,
                    Message = "Render completed!",
                    IsComplete = true
                });

                return config.OutputPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error rendering video");
                throw;
            }
        }, cancellationToken);
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
    /// Build FFmpeg command line arguments.
    /// </summary>
    private static string BuildFFmpegCommand(RenderConfig config)
    {
        var crf = config.GetCrfValue();

        // FFmpeg command structure:
        // ffmpeg -loop 1 -i image.jpg -i audio.mp3 -c:v codec -crf value -vf scale=w:h -r fps -c:a codec -shortest output.mp4
        
        var args = $"-loop 1 " +
                  $"-i \"{config.ImagePath}\" " +
                  $"-i \"{config.AudioPath}\" " +
                  $"-c:v {config.VideoCodec} " +
                  $"-crf {crf} " +
                  $"-vf \"scale={config.ResolutionWidth}:{config.ResolutionHeight}\" " +
                  $"-r {config.FrameRate} " +
                  $"-c:a {config.AudioCodec} " +
                  $"-shortest " +
                  $"-y " + // Overwrite output file
                  $"\"{config.OutputPath}\"";

        return args;
    }

    /// <summary>
    /// Execute FFmpeg command and track progress.
    /// </summary>
    private static async Task<(bool success, string output)> ExecuteFFmpegAsync(
        string ffmpegPath,
        string args,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
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
                    CreateNoWindow = true
                };

                _currentRenderProcess = Process.Start(processInfo);
                if (_currentRenderProcess == null)
                    return (false, "Failed to start FFmpeg process");

                var output = string.Empty;
                var error = string.Empty;

                // Read output asynchronously
                var outputTask = _currentRenderProcess.StandardOutput.ReadToEndAsync();
                var errorTask = _currentRenderProcess.StandardError.ReadToEndAsync();

                // Report progress (simplified - FFmpeg doesn't provide easy frame count)
                progress?.Report(new RenderProgress
                {
                    ProgressPercentage = 50,
                    Message = "Rendering...",
                    IsComplete = false
                });

                // Wait for completion
                _currentRenderProcess.WaitForExit();

                output = outputTask.Result;
                error = errorTask.Result;

                bool success = _currentRenderProcess.ExitCode == 0;
                return (success, success ? output : error);
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
        }, cancellationToken);
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
