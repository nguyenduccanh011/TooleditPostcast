#nullable enable
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Builds FFmpeg command-line arguments and filter_complex graphs from a <see cref="RenderConfig"/>.
/// Extracted from FFmpegService to keep that class focused on path detection, validation,
/// and process execution only.
/// </summary>
public static class FFmpegCommandComposer
{
    private static readonly object _encoderProbeLock = new();
    private static string? _preferredH264Encoder;
    private static string? _preferredHevcEncoder;

    /// <summary>
    /// Normalize and build the complete FFmpeg argument string for a render config.
    /// Entry point used by <see cref="FFmpegService.RenderVideoAsync"/>.
    /// </summary>
    public static (string args, RenderConfig normalizedConfig) Build(RenderConfig config)
    {
        var normalized = NormalizeRenderConfig(config);
        var args = BuildFFmpegCommand(normalized);
        return (args, normalized);
    }

    // ── Command builders ────────────────────────────────────────────────

    private static string BuildFFmpegCommand(RenderConfig config)
    {
        var hasVisual = config.VisualSegments != null && config.VisualSegments.Count > 0;
        var hasText   = config.TextSegments  != null && config.TextSegments.Count  > 0;
        var hasAudio  = config.AudioSegments != null && config.AudioSegments.Count > 0;

        if (hasVisual || hasText || hasAudio)
            return BuildTimelineFFmpegCommand(config);

        var crf = config.GetCrfValue();
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

        if (visualSegments.Count == 0 && textSegments.Count == 0 && audioSegments.Count == 0)
            return BuildLegacySingleImageCommand(config, crf);

        var args = new StringBuilder();

        // ── Input 0: black background canvas
        args.Append($"-f lavfi -i \"color=c=black:s={config.ResolutionWidth}x{config.ResolutionHeight}:r={config.FrameRate}:d=86400\" ");

        // ── Inputs 1..N : visual sources
        foreach (var seg in visualSegments)
        {
            if (seg.IsVideo)
                args.Append($"-i \"{seg.SourcePath}\" ");
            else
                args.Append($"-loop 1 -i \"{seg.SourcePath}\" ");
        }

        // ── Input N+1: primary audio or silent placeholder
        var hasPrimaryAudio = !string.IsNullOrWhiteSpace(config.AudioPath) && File.Exists(config.AudioPath);
        var primaryAudioIndex = visualSegments.Count + 1;
        if (hasPrimaryAudio)
            args.Append($"-i \"{config.AudioPath}\" ");
        else
            args.Append($"-f lavfi -i \"anullsrc=r=44100:cl=stereo\" ");

        // ── Extra audio clip inputs
        var extraAudioStartIndex = primaryAudioIndex + 1;
        foreach (var aseg in audioSegments)
            args.Append($"-i \"{aseg.SourcePath}\" ");

        // ── filter_complex
        var filter = new StringBuilder();

        // Step 1: base canvas
        filter.Append($"[0:v]format=yuv420p,setsar=1[base];");

        // Step 2: Scale + position each visual segment
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var inputIdx = i + 1;
            var seg = visualSegments[i];
            var start    = seg.StartTime.ToString("F3", invariant);
            var end      = seg.EndTime.ToString("F3", invariant);
            var duration = (seg.EndTime - seg.StartTime).ToString("F3", invariant);
            var srcOffset = Math.Max(0, seg.SourceOffsetSeconds).ToString("F3", invariant);
            var scaledLabel = $"scaled{i}";

            var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                : BuildScalingFilter(config);

            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var pixFmt = (isPngOverlay || seg.HasAlpha) ? "rgba" : "yuv420p";

            if (seg.IsVideo)
            {
                filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                filter.Append($"format={pixFmt},{scaleFilter},setsar=1[{scaledLabel}];");
            }
            else
            {
                filter.Append($"[{inputIdx}:v]format={pixFmt},{scaleFilter},setsar=1[{scaledLabel}];");
            }
        }

        // Step 3: Overlay each visual onto the base
        var currentVideo = "base";
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var seg     = visualSegments[i];
            var start   = seg.StartTime.ToString("F3", invariant);
            var end     = seg.EndTime.ToString("F3", invariant);
            var outLabel = $"v{i}";

            var overlayX = seg.OverlayX ?? "0";
            var overlayY = seg.OverlayY ?? "0";
            var overlayPos = $"x={overlayX}:y={overlayY}:";

            if (!seg.IsVideo)
            {
                var scaledPtsLabel = $"scaledpts{i}";
                filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{scaledPtsLabel}];");
                filter.Append($"[{currentVideo}][{scaledPtsLabel}]overlay={overlayPos}format=auto:shortest=0:eof_action=pass:enable='between(t,{start},{end})'[{outLabel}];");
            }
            else
            {
                var shiftedLabel = $"shifted{i}";
                filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{shiftedLabel}];");
                filter.Append($"[{currentVideo}][{shiftedLabel}]overlay={overlayPos}format=auto:shortest=0:eof_action=pass:enable='between(t,{start},{end})'[{outLabel}];");
            }
            currentVideo = outLabel;
        }

        // Resolve a default font once for all text segments
        string? resolvedDefaultFont = ResolveDefaultFontPath();

        // Step 4: Text overlays via drawtext
        var textTempDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "render_text");
        if (textSegments.Count > 0)
            Directory.CreateDirectory(textTempDir);

        for (int i = 0; i < textSegments.Count; i++)
        {
            var ts = textSegments[i];
            var start = ts.StartTime.ToString("F3", invariant);
            var end   = ts.EndTime.ToString("F3", invariant);
            var outLabel = $"t{i}";

            var textFilePath = Path.Combine(textTempDir, $"seg_{i}.txt");
            File.WriteAllText(textFilePath, ts.Text, new UTF8Encoding(false));

            var fontPath = ts.FontFilePath;
            if (string.IsNullOrWhiteSpace(fontPath))
                fontPath = resolvedDefaultFont;

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

        // Step 5: Audio mixing
        string audioOut;
        if (audioSegments.Count == 0)
        {
            var primVolOnly = config.PrimaryAudioVolume.ToString("F3", invariant);
            filter.Append($"[{primaryAudioIndex}:a]volume={primVolOnly}[amain];");
            audioOut = "amain";
        }
        else
        {
            var primVol = config.PrimaryAudioVolume.ToString("F3", invariant);
            filter.Append($"[{primaryAudioIndex}:a]volume={primVol},aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[amain];");

            var mixLabels = new List<string> { "amain" };
            for (int i = 0; i < audioSegments.Count; i++)
            {
                var aseg        = audioSegments[i];
                var inputIdx    = extraAudioStartIndex + i;
                var delayMs     = (long)Math.Round(aseg.StartTime * 1000);
                var duration    = aseg.EndTime - aseg.StartTime;
                var srcOffset   = Math.Max(0, aseg.SourceOffsetSeconds).ToString("F3", invariant);
                var clipLabel   = $"aclip{i}";

                if (aseg.IsLooping)
                {
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
            filter.Append($"{allMixInputs}amix=inputs={mixLabels.Count}:duration=first:dropout_transition=0:normalize=0[{audioOut}]");
        }

        var filterStr = filter.ToString().TrimEnd(';');

        // Write filter to a temp script file
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor",
            $"filter_{Path.GetFileNameWithoutExtension(config.OutputPath)}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new UTF8Encoding(false));
        Log.Debug("Filter script written to: {Path}\n{Content}", filterScriptPath, filterStr);

        args.Append($"-filter_complex_script \"{filterScriptPath}\" ");

        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");

        args.Append($"-c:v {videoCodec} ");
        args.Append($"-crf {crf} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");
        args.Append("-movflags +faststart ");
        if (!hasPrimaryAudio)
        {
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

    // ── Normalisation ───────────────────────────────────────────────────

    public static RenderConfig NormalizeRenderConfig(RenderConfig config)
    {
        var normalizedAspect = RenderSizing.NormalizeAspectRatio(config.AspectRatio);
        var (evenWidth, evenHeight) = RenderSizing.EnsureEvenDimensions(config.ResolutionWidth, config.ResolutionHeight);
        var frameRate = config.FrameRate <= 0 ? 30 : Math.Min(config.FrameRate, 120);

        if (evenWidth != config.ResolutionWidth || evenHeight != config.ResolutionHeight)
        {
            Log.Warning(
                "Adjusted render size from {OriginalWidth}x{OriginalHeight} to even dimensions {Width}x{Height}",
                config.ResolutionWidth, config.ResolutionHeight, evenWidth, evenHeight);
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

    private static string NormalizeQuality(string quality) => quality switch
    {
        "Low" => "Low",
        "Medium" => "Medium",
        "High" => "High",
        _ => "Medium"
    };

    private static string NormalizeScaleMode(string scaleMode) => scaleMode switch
    {
        "Fit" => "Fit",
        "Stretch" => "Stretch",
        _ => "Fill"
    };

    // ── Codec mapping ───────────────────────────────────────────────────

    internal static string MapVideoCodec(string videoCodec)
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

    internal static string MapAudioCodec(string audioCodec)
    {
        if (string.IsNullOrWhiteSpace(audioCodec))
            return "aac";

        return audioCodec.Trim().ToLowerInvariant() switch
        {
            "mp3" => "libmp3lame",
            _ => audioCodec
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

            var ffmpegPath = FFmpegService.GetFFmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
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

    // ── Filter helpers ──────────────────────────────────────────────────

    internal static string BuildScalingFilter(RenderConfig config) => config.ScaleMode switch
    {
        "Fit" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=decrease,pad={config.ResolutionWidth}:{config.ResolutionHeight}:(ow-iw)/2:(oh-ih)/2",
        "Stretch" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}",
        _ => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=increase,crop={config.ResolutionWidth}:{config.ResolutionHeight}"
    };

    internal static string EscapeFilterPath(string path) =>
        path.Replace("\\", "/")
            .Replace("'",  "\\'")
            .Replace(":",  "\\:");

    private static string? ResolveDefaultFontPath()
    {
        var winFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string[] candidates = ["arial.ttf", "segoeui.ttf", "calibri.ttf", "tahoma.ttf", "verdana.ttf"];
        foreach (var font in candidates)
        {
            var path = Path.Combine(winFonts, font);
            if (File.Exists(path))
                return path;
        }
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
}
