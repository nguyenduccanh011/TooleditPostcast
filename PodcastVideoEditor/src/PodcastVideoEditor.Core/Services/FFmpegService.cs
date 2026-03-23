#nullable enable
using Serilog;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    private static readonly object _encoderProbeLock = new();
    private static string? _preferredH264Encoder;
    private static string? _preferredHevcEncoder;

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

        var normalizedConfig = NormalizeRenderConfig(config);

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

                // Build FFmpeg command
                var ffmpegArgs = BuildFFmpegCommand(normalizedConfig);

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

            // Delete text segment temp files
            var textDir = Path.Combine(tempDir, "render_text");
            if (Directory.Exists(textDir))
                Directory.Delete(textDir, recursive: true);
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
    /// Build FFmpeg command line arguments.
    /// </summary>
    private static string BuildFFmpegCommand(RenderConfig config)
    {
        // Route to timeline composer when ANY segment type exists
        var hasVisual = config.VisualSegments != null && config.VisualSegments.Count > 0;
        var hasText   = config.TextSegments  != null && config.TextSegments.Count  > 0;
        var hasAudio  = config.AudioSegments != null && config.AudioSegments.Count > 0;

        if (hasVisual || hasText || hasAudio)
            return BuildTimelineFFmpegCommand(config);

        var crf = config.GetCrfValue();
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var scaleFilter = BuildScalingFilter(config);

        // FFmpeg command structure:
        // ffmpeg -loop 1 -i image.jpg -i audio.mp3 -c:v codec -crf value -vf scale=w:h -r fps -c:a codec -shortest output.mp4
        
        var args = $"-loop 1 " +
                  $"-i \"{config.ImagePath}\" " +
                  $"-i \"{config.AudioPath}\" " +
                  $"-c:v {videoCodec} " +
                  $"-crf {crf} " +
                  $"-vf \"{scaleFilter},setsar=1\" " +
                  $"-pix_fmt yuv420p " +
                  $"-r {config.FrameRate} " +
                  $"-c:a {audioCodec} " +
                  "-movflags +faststart " +
                  $"-shortest " +
                  $"-y " + // Overwrite output file
                  $"\"{config.OutputPath}\"";

        return args;
    }

    /// <summary>
    /// Build FFmpeg command for timeline-based visual composition.
    /// Handles multi-track visuals, text overlays (drawtext), and extra audio clips (adelay+amix).
    /// </summary>
    private static string BuildTimelineFFmpegCommand(RenderConfig config)
    {
        var crf = config.GetCrfValue();
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var invariant = CultureInfo.InvariantCulture;

        var visualSegments = (config.VisualSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .OrderBy(s => s.ZOrder)
            .ThenBy(s => s.StartTime)
            .ToList();

        var textSegments = (config.TextSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.EndTime > s.StartTime)
            .OrderBy(s => s.StartTime)
            .ToList();

        var audioSegments = (config.AudioSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .OrderBy(s => s.StartTime)
            .ToList();

        // When there are no visual segments AND no text/audio segments, fall back to legacy
        if (visualSegments.Count == 0 && textSegments.Count == 0 && audioSegments.Count == 0)
            return BuildLegacySingleImageCommand(config, crf);

        var args = new StringBuilder();

        // ── Input 0: black background canvas ──────────────────────────────
        args.Append($"-f lavfi -i \"color=c=black:s={config.ResolutionWidth}x{config.ResolutionHeight}:r={config.FrameRate}:d=86400\" ");

        // ── Inputs 1..N : visual sources ──────────────────────────────────
        foreach (var seg in visualSegments)
        {
            if (seg.IsVideo)
                args.Append($"-i \"{seg.SourcePath}\" ");
            else
                // -loop 1 so the still image is available for the full timeline
                args.Append($"-loop 1 -i \"{seg.SourcePath}\" ");
        }

        // ── Input N+1: primary audio (project audio file) or silent placeholder ──
        var hasPrimaryAudio = !string.IsNullOrWhiteSpace(config.AudioPath) && File.Exists(config.AudioPath);
        var primaryAudioIndex = visualSegments.Count + 1;
        if (hasPrimaryAudio)
            args.Append($"-i \"{config.AudioPath}\" ");
        else
            args.Append($"-f lavfi -i \"anullsrc=r=44100:cl=stereo\" ");

        // ── Extra audio clip inputs ────────────────────────────────────────
        var extraAudioStartIndex = primaryAudioIndex + 1;
        foreach (var aseg in audioSegments)
            args.Append($"-i \"{aseg.SourcePath}\" ");

        // ── filter_complex ─────────────────────────────────────────────────
        var filter = new StringBuilder();

        // Step 1: Prepare base canvas — normalise pixel format once
        filter.Append($"[0:v]format=yuv420p,setsar=1[base];");

        // Step 2: Scale + position each visual segment using enable= for correct timing.
        // BUG-4 fix: use overlay enable='between(t,start,end)' + setpts=PTS-STARTPTS
        //   so images are composited only within their window, pixel-format-safe.
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var inputIdx = i + 1;
            var seg = visualSegments[i];
            var start    = seg.StartTime.ToString("F3", invariant);
            var end      = seg.EndTime.ToString("F3", invariant);
            var duration = (seg.EndTime - seg.StartTime).ToString("F3", invariant);
            var srcOffset = Math.Max(0, seg.SourceOffsetSeconds).ToString("F3", invariant);
            var scaledLabel = $"scaled{i}";

            // Use segment-specific scale if provided, otherwise full-frame
            var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                : BuildScalingFilter(config);

            // PNG overlays (e.g. rasterized text) and baked visualizer videos have alpha channels
            // and require yuva420p so the transparency is preserved during compositing.
            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var pixFmt = (isPngOverlay || seg.HasAlpha) ? "yuva420p" : "yuv420p";

            if (seg.IsVideo)
            {
                // Trim source to its window, reset PTS to 0
                filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                filter.Append($"format={pixFmt},{scaleFilter},setsar=1[{scaledLabel}];");
            }
            else
            {
                filter.Append($"[{inputIdx}:v]format={pixFmt},{scaleFilter},setsar=1[{scaledLabel}];");
            }
        }

        // Step 3: Overlay each visual onto the base using enable= range
        var currentVideo = "base";
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var seg     = visualSegments[i];
            var start   = seg.StartTime.ToString("F3", invariant);
            var end     = seg.EndTime.ToString("F3", invariant);
            var outLabel = $"v{i}";

            // Build overlay position: use segment X/Y if provided, otherwise default (0,0)
            var overlayX = seg.OverlayX ?? "0";
            var overlayY = seg.OverlayY ?? "0";
            var overlayPos = $"x={overlayX}:y={overlayY}:";

            // For images: shift PTS so the frame appears at the right time in the output
            if (!seg.IsVideo)
            {
                var scaledPtsLabel = $"scaledpts{i}";
                filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{scaledPtsLabel}];");
                filter.Append($"[{currentVideo}][{scaledPtsLabel}]overlay={overlayPos}shortest=0:eof_action=pass:enable='between(t,{start},{end})'[{outLabel}];");
            }
            else
            {
                // Video: already trimmed+PTS-reset; shift into output timeline
                var shiftedLabel = $"shifted{i}";
                filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{shiftedLabel}];");
                filter.Append($"[{currentVideo}][{shiftedLabel}]overlay={overlayPos}shortest=0:eof_action=pass:enable='between(t,{start},{end})'[{outLabel}];");
            }
            currentVideo = outLabel;
        }

        // Resolve a default font once for all text segments
        string? resolvedDefaultFont = ResolveDefaultFontPath();

        // Step 4: Text overlays via drawtext — BUG-1 fix
        // Text content is written to temp UTF-8 files (textfile=) so Vietnamese/Unicode
        // renders correctly regardless of Windows console codepage.
        var textTempDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "render_text");
        if (textSegments.Count > 0)
            Directory.CreateDirectory(textTempDir);

        for (int i = 0; i < textSegments.Count; i++)
        {
            var ts = textSegments[i];
            var start = ts.StartTime.ToString("F3", invariant);
            var end   = ts.EndTime.ToString("F3", invariant);
            var outLabel = $"t{i}";

            // Write text to a temp UTF-8 file — avoids command-line encoding corruption
            var textFilePath = Path.Combine(textTempDir, $"seg_{i}.txt");
            File.WriteAllText(textFilePath, ts.Text, new System.Text.UTF8Encoding(false));

            var fontPath = ts.FontFilePath;
            if (string.IsNullOrWhiteSpace(fontPath))
                fontPath = resolvedDefaultFont;

            // Escape file paths for FFmpeg filter syntax:
            //  `:` → `\:` (literal `\:` in file = FFmpeg sees `\:` = escaped colon)
            //  `'` → `\'`
            var fontfileArg = string.IsNullOrWhiteSpace(fontPath)
                ? string.Empty
                : $"fontfile='{EscapeFilterPath(fontPath)}':";

            var escapedTextFilePath = EscapeFilterPath(textFilePath);

            var boxArg = ts.DrawBox
                ? $"box=1:boxcolor={ts.BoxColor}:boxborderw=10:"
                : string.Empty;

            filter.Append($"[{currentVideo}]drawtext={fontfileArg}textfile='{escapedTextFilePath}':" +
                          $"fontsize={ts.FontSize}:fontcolor={ts.FontColor}:" +
                          $"{boxArg}" +
                          $"x={ts.XExpr}:y={ts.YExpr}:" +
                          $"enable='between(t,{start},{end})'[{outLabel}];");
            currentVideo = outLabel;
        }

        // Step 5: Audio mixing — BUG-2 fix
        // Build audio filter: start with primary audio, then mix in extra clips via adelay+amix
        string audioOut;
        if (audioSegments.Count == 0)
        {
            // No extra audio clips — route primary through volume filter so PrimaryAudioVolume is honoured.
            var primVolOnly = config.PrimaryAudioVolume.ToString("F3", invariant);
            filter.Append($"[{primaryAudioIndex}:a]volume={primVolOnly}[amain];");
            audioOut = "amain";
        }
        else
        {
            // Delay each extra audio clip to its timeline position and amix all together
            var primVol = config.PrimaryAudioVolume.ToString("F3", invariant);
            filter.Append($"[{primaryAudioIndex}:a]volume={primVol},aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[amain];");

            var mixLabels = new System.Collections.Generic.List<string> { "amain" };
            for (int i = 0; i < audioSegments.Count; i++)
            {
                var aseg        = audioSegments[i];
                var inputIdx    = extraAudioStartIndex + i;
                var delayMs     = (long)Math.Round(aseg.StartTime * 1000);
                var duration    = aseg.EndTime - aseg.StartTime;
                var srcOffset   = Math.Max(0, aseg.SourceOffsetSeconds).ToString("F3", invariant);
                var clipLabel   = $"aclip{i}";

                // Trim clip to its window, apply volume and optional fades, then delay to output position
                // For looping audio (e.g. BGM), use aloop to repeat until it fills the duration window
                if (aseg.IsLooping)
                {
                    // aloop loops=N:size=whole-file. We set loops high enough to cover duration,
                    // then atrim the result to exact duration.
                    filter.Append($"[{inputIdx}:a]aloop=loop=-1:size=2147483647,");
                    filter.Append($"atrim=start=0:duration={duration.ToString("F3", invariant)},");
                }
                else
                {
                    filter.Append($"[{inputIdx}:a]atrim=start={srcOffset}:duration={duration.ToString("F3", invariant)},");
                }
                filter.Append($"asetpts=PTS-STARTPTS,");
                filter.Append($"volume={aseg.Volume.ToString("F3", invariant)},");
                if (aseg.FadeInDuration > 0)
                    filter.Append($"afade=t=in:st=0:d={aseg.FadeInDuration.ToString("F3", invariant)},");
                if (aseg.FadeOutDuration > 0)
                    filter.Append($"afade=t=out:st={(duration - aseg.FadeOutDuration).ToString("F3", invariant)}:d={aseg.FadeOutDuration.ToString("F3", invariant)},");
                filter.Append($"adelay={delayMs}|{delayMs},");
                filter.Append($"aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[{clipLabel}];");
                mixLabels.Add(clipLabel);
            }

            var allMixInputs = string.Concat(mixLabels.Select(l => $"[{l}]"));
            audioOut = "amixed";
            // normalize=0: disable FFmpeg's default 1/N gain reduction so each track's
            // explicit volume= filter value is respected at face value.
            filter.Append($"{allMixInputs}amix=inputs={mixLabels.Count}:duration=first:dropout_transition=0:normalize=0[{audioOut}]");
        }

        // BUG-5 fix: strip trailing semicolon
        var filterStr = filter.ToString().TrimEnd(';');

        // Write filter to a temp script file — this avoids:
        //   1) command-line encoding issues with Unicode text file paths
        //   2) command-line length limits for complex filter graphs
        //   3) nested quote/escape hell between C#, cmd.exe, and FFmpeg
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor",
            $"filter_{Path.GetFileNameWithoutExtension(config.OutputPath)}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new System.Text.UTF8Encoding(false));
        Log.Debug("Filter script written to: {Path}\n{Content}", filterScriptPath, filterStr);

        args.Append($"-filter_complex_script \"{filterScriptPath}\" ");

        // Map outputs — audioOut is always a filter label now, always needs brackets
        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");

        args.Append($"-c:v {videoCodec} ");
        args.Append($"-crf {crf} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");
        args.Append("-movflags +faststart ");
        if (!hasPrimaryAudio)
        {
            // No finite audio file to bound output duration — compute from segments
            var allEndTimes = visualSegments.Select(s => s.EndTime)
                .Concat(textSegments.Select(s => s.EndTime))
                .Concat(audioSegments.Select(s => s.EndTime));
            var maxEnd = allEndTimes.Any() ? allEndTimes.Max() : 1.0;
            args.Append($"-t {maxEnd.ToString("F3", invariant)} ");
        }
        args.Append("-shortest -y ");
        args.Append($"\"{config.OutputPath}\"");

        return args.ToString();
    }

    private static string BuildLegacySingleImageCommand(RenderConfig config, int crf)
    {
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var scaleFilter = BuildScalingFilter(config);
        return $"-loop 1 " +
               $"-i \"{config.ImagePath}\" " +
               $"-i \"{config.AudioPath}\" " +
               $"-c:v {videoCodec} " +
               $"-crf {crf} " +
               $"-vf \"{scaleFilter},setsar=1\" " +
               "-pix_fmt yuv420p " +
               $"-r {config.FrameRate} " +
               $"-c:a {audioCodec} " +
               "-movflags +faststart " +
               $"-shortest " +
               $"-y " +
               $"\"{config.OutputPath}\"";
    }

    private static RenderConfig NormalizeRenderConfig(RenderConfig config)
    {
        var normalizedAspect = RenderSizing.NormalizeAspectRatio(config.AspectRatio);
        var (evenWidth, evenHeight) = RenderSizing.EnsureEvenDimensions(config.ResolutionWidth, config.ResolutionHeight);
        var frameRate = config.FrameRate <= 0 ? 30 : Math.Min(config.FrameRate, 120);

        if (evenWidth != config.ResolutionWidth || evenHeight != config.ResolutionHeight)
        {
            Log.Warning(
                "Adjusted render size from {OriginalWidth}x{OriginalHeight} to even dimensions {Width}x{Height}",
                config.ResolutionWidth,
                config.ResolutionHeight,
                evenWidth,
                evenHeight);
        }

        return new RenderConfig
        {
            AudioPath = config.AudioPath,
            ImagePath = config.ImagePath,
            OutputPath = config.OutputPath,
            ResolutionWidth = evenWidth,
            ResolutionHeight = evenHeight,
            AspectRatio = normalizedAspect,
            Quality = NormalizeQuality(config.Quality),
            FrameRate = frameRate,
            VideoCodec = string.IsNullOrWhiteSpace(config.VideoCodec) ? "h264_auto" : config.VideoCodec,
            AudioCodec = string.IsNullOrWhiteSpace(config.AudioCodec) ? "aac" : config.AudioCodec,
            ScaleMode = NormalizeScaleMode(config.ScaleMode),
            PrimaryAudioVolume = config.PrimaryAudioVolume,
            VisualSegments = config.VisualSegments?.ToList() ?? [],
            TextSegments   = config.TextSegments?.ToList()  ?? [],
            AudioSegments  = config.AudioSegments?.ToList() ?? []
        };
    }

    private static string NormalizeQuality(string quality)
    {
        return quality switch
        {
            "Low" => "Low",
            "Medium" => "Medium",
            "High" => "High",
            _ => "Medium"
        };
    }

    private static string NormalizeScaleMode(string scaleMode)
    {
        return scaleMode switch
        {
            "Fit" => "Fit",
            "Stretch" => "Stretch",
            _ => "Fill"
        };
    }

    private static string MapVideoCodec(string videoCodec)
    {
        if (string.IsNullOrWhiteSpace(videoCodec))
            return GetPreferredH264Encoder();

        return videoCodec.Trim().ToLowerInvariant() switch
        {
            "h264_auto" => GetPreferredH264Encoder(),
            "hevc_auto" => GetPreferredHevcEncoder(),
            "h264" => "libx264",
            "x264" => "libx264",
            "h265" => "libx265",
            "hevc" => "libx265",
            "x265" => "libx265",
            "h264_nvenc" => "h264_nvenc",
            "hevc_nvenc" => "hevc_nvenc",
            "h264_qsv" => "h264_qsv",
            "hevc_qsv" => "hevc_qsv",
            "h264_amf" => "h264_amf",
            "hevc_amf" => "hevc_amf",
            _ => videoCodec
        };
    }

    private static string GetPreferredH264Encoder()
    {
        EnsurePreferredEncodersInitialized();
        return _preferredH264Encoder ?? "libx264";
    }

    private static string GetPreferredHevcEncoder()
    {
        EnsurePreferredEncodersInitialized();
        return _preferredHevcEncoder ?? "libx265";
    }

    private static void EnsurePreferredEncodersInitialized()
    {
        if (!string.IsNullOrEmpty(_preferredH264Encoder) && !string.IsNullOrEmpty(_preferredHevcEncoder))
            return;

        lock (_encoderProbeLock)
        {
            if (!string.IsNullOrEmpty(_preferredH264Encoder) && !string.IsNullOrEmpty(_preferredHevcEncoder))
                return;

            _preferredH264Encoder = "libx264";
            _preferredHevcEncoder = "libx265";

            if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(5));

                if (output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase))
                    _preferredH264Encoder = "h264_nvenc";
                else if (output.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase))
                    _preferredH264Encoder = "h264_qsv";
                else if (output.Contains("h264_amf", StringComparison.OrdinalIgnoreCase))
                    _preferredH264Encoder = "h264_amf";

                if (output.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
                    _preferredHevcEncoder = "hevc_nvenc";
                else if (output.Contains("hevc_qsv", StringComparison.OrdinalIgnoreCase))
                    _preferredHevcEncoder = "hevc_qsv";
                else if (output.Contains("hevc_amf", StringComparison.OrdinalIgnoreCase))
                    _preferredHevcEncoder = "hevc_amf";

                Log.Information("Preferred encoders: H264={H264}, HEVC={HEVC}", _preferredH264Encoder, _preferredHevcEncoder);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to probe FFmpeg hardware encoders. Falling back to software encoders.");
            }
        }
    }

    private static string MapAudioCodec(string audioCodec)
    {
        if (string.IsNullOrWhiteSpace(audioCodec))
            return "aac";

        return audioCodec.Trim().ToLowerInvariant() switch
        {
            "mp3" => "libmp3lame",
            _ => audioCodec
        };
    }

    private static string BuildScalingFilter(RenderConfig config)
    {
        return config.ScaleMode switch
        {
            "Fit" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=decrease,pad={config.ResolutionWidth}:{config.ResolutionHeight}:(ow-iw)/2:(oh-ih)/2",
            "Stretch" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}",
            _ => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=increase,crop={config.ResolutionWidth}:{config.ResolutionHeight}"
        };
    }

    /// <summary>
    /// Escape a file path for use inside an FFmpeg filter option value.
    /// Colons (e.g. `C:`) and single quotes must be escaped so FFmpeg's filter parser
    /// doesn't misinterpret them as option separators or string delimiters.
    /// </summary>
    private static string EscapeFilterPath(string path)
    {
        return path
            .Replace("\\", "/")       // normalise to forward slashes
            .Replace("'",  "\\'")     // escape single quotes
            .Replace(":",  "\\:");    // escape colon (critical for Windows drive letters like C:)
    }

    /// <summary>
    /// Resolve a default font file path for FFmpeg drawtext on the current OS.
    /// Most FFmpeg Windows builds lack fontconfig, so a bare font name like "Arial" won't work;
    /// an explicit .ttf path is required.
    /// </summary>
    private static string? ResolveDefaultFontPath()
    {
        // Windows: try system fonts directory
        var winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        // Prefer common fonts in order of relevance
        string[] candidates = ["arial.ttf", "segoeui.ttf", "calibri.ttf", "tahoma.ttf", "verdana.ttf"];
        foreach (var font in candidates)
        {
            var path = Path.Combine(winFonts, font);
            if (File.Exists(path))
                return path;
        }
        // Linux/Mac fallback
        string[] unixCandidates = [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/System/Library/Fonts/Helvetica.ttc"
        ];
        foreach (var path in unixCandidates)
        {
            if (File.Exists(path))
                return path;
        }
        Log.Warning("No default font found for drawtext — text overlays may fail");
        return null;
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
                CreateNoWindow = true
            };

            _currentRenderProcess = Process.Start(processInfo);
            if (_currentRenderProcess == null)
                return (false, "Failed to start FFmpeg process");

            // Read stdout fully in background (usually empty for FFmpeg)
            var outputTask = _currentRenderProcess.StandardOutput.ReadToEndAsync(cancellationToken);

            // Parse stderr line-by-line for progress
            var stderrLines = new System.Collections.Generic.List<string>();
            double totalDurationSeconds = 0;

            // First pass: try to detect total duration from the audio input headers
            // (FFmpeg prints "Duration: HH:MM:SS.mm" for each input early in stderr)
            var stderrReader = _currentRenderProcess.StandardError;
            string? line;
            while ((line = await stderrReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                stderrLines.Add(line);

                // Detect total duration from the FIRST "Duration:" line (usually the audio input)
                if (totalDurationSeconds <= 0 && line.Contains("Duration:"))
                {
                    var durationMatch = System.Text.RegularExpressions.Regex.Match(
                        line, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                    if (durationMatch.Success &&
                        int.TryParse(durationMatch.Groups[1].Value, out var dh) &&
                        int.TryParse(durationMatch.Groups[2].Value, out var dm) &&
                        double.TryParse(durationMatch.Groups[3].Value,
                            System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var ds))
                    {
                        totalDurationSeconds = dh * 3600 + dm * 60 + ds;
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
                        int pct = totalDurationSeconds > 0
                            ? (int)Math.Min(99, encodedSeconds / totalDurationSeconds * 100)
                            : 50;

                        progress?.Report(new RenderProgress
                        {
                            ProgressPercentage = pct,
                            Message = $"Rendering... {pct}%",
                            IsComplete = false
                        });
                    }
                }
            }

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
