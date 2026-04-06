#nullable enable
#pragma warning disable CS0618 // RenderConfig.TextSegments is intentionally kept for legacy compatibility paths.
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Builds FFmpeg filter_complex graphs from a <see cref="CompositionPlan"/>.
/// Separates filter graph generation from FFmpeg process management (SRP).
///
/// Each layer type (Image, Video, Visualizer, RasterizedText) is processed
/// by a dedicated method — equivalent to a strategy pattern per source type.
///
/// Commercial pattern: DaVinci Resolve uses a Fusion node graph;
/// this builder produces an equivalent linear overlay chain for FFmpeg.
/// </summary>
public static class FFmpegFilterGraphBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Build a complete FFmpeg command from a <see cref="CompositionPlan"/>.
    /// </summary>
    public static string BuildCommand(CompositionPlan plan)
    {
        var config = plan.ToRenderConfig();
        return BuildCommandFromConfig(config, plan.ScaleMode);
    }

    /// <summary>
    /// Build FFmpeg command from a legacy <see cref="RenderConfig"/>.
    /// Delegates to the existing FFmpegService pipeline for backward compatibility.
    /// </summary>
    internal static string BuildCommandFromConfig(RenderConfig config, string globalScaleMode)
    {
        var crf = config.GetCrfValue();
        var videoCodec = FFmpegCodecHelper.MapVideoCodec(config.VideoCodec);
        var audioCodec = FFmpegCodecHelper.MapAudioCodec(config.AudioCodec);

        var visualSegments = FilterAndSortVisualSegments(config);
        var textSegments = FilterAndSortTextSegments(config);
        var audioSegments = FilterAndSortAudioSegments(config);

        if (visualSegments.Count == 0 && textSegments.Count == 0 && audioSegments.Count == 0)
            return BuildLegacySingleImageCommand(config, crf, videoCodec, audioCodec);

        var args = new StringBuilder();

        // ── Inputs ──────────────────────────────────────────────────────────
        AppendInputs(args, config, visualSegments, audioSegments,
            out int primaryAudioIndex, out bool hasPrimaryAudio, out int extraAudioStartIndex);

        // ── filter_complex ──────────────────────────────────────────────────
        var filter = new StringBuilder();

        // Step 1: Base canvas
        filter.Append($"[0:v]format=yuv420p,setsar=1[base];");

        // Step 2: Scale each visual source
        BuildVisualScaleFilters(filter, visualSegments, config);

        // Step 3: Overlay each visual onto the base
        var currentVideo = BuildVisualOverlayChain(filter, visualSegments);

        // Step 4: Text overlays via drawtext
        currentVideo = BuildTextOverlays(filter, textSegments, currentVideo);

        // Step 5: Audio mixing
        var audioOut = BuildAudioMix(filter, audioSegments, primaryAudioIndex, config.PrimaryAudioVolume);

        // Write filter script to temp file
        var filterStr = filter.ToString().TrimEnd(';');
        var filterScriptPath = WriteFilterScript(filterStr, config.OutputPath);

        // -filter_complex_script was removed in FFmpeg 7.1; use -/filter_complex
        // to read the graph from a file.
        args.Append($"-/filter_complex \"{filterScriptPath}\" ");
        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");
        args.Append($"-c:v {videoCodec} ");
        args.Append(FFmpegCommandComposer.BuildQualityArgs(videoCodec, crf));
        var preset = FFmpegCommandComposer.GetEncoderPreset(videoCodec, config.Quality);
        if (!string.IsNullOrEmpty(preset))
            args.Append($"-preset {preset} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");
        // Threading: reserve 2 cores for OS/UI, scale filter threads to CPU count.
        var logicalCores = Environment.ProcessorCount;
        var renderThreads = Math.Max(2, logicalCores - 2);
        var filterThreads = Math.Max(2, logicalCores / 2);
        args.Append($"-threads {renderThreads} ");
        args.Append($"-filter_threads {filterThreads} ");
        args.Append($"-filter_complex_threads {filterThreads} ");
        args.Append("-movflags +faststart ");

        if (!hasPrimaryAudio)
        {
            var allEndTimes = visualSegments.Select(s => s.EndTime)
                .Concat(textSegments.Select(s => s.EndTime))
                .Concat(audioSegments.Select(s => s.EndTime));
            var maxEnd = allEndTimes.Any() ? allEndTimes.Max() : 1.0;
            args.Append($"-t {maxEnd.ToString("F3", Inv)} ");
        }

        args.Append("-shortest -y ");
        args.Append($"\"{config.OutputPath}\"");

        return args.ToString();
    }

    // ─── Input building ────────────────────────────────────────────────────

    private static void AppendInputs(
        StringBuilder args,
        RenderConfig config,
        List<RenderVisualSegment> visualSegments,
        List<RenderAudioSegment> audioSegments,
        out int primaryAudioIndex,
        out bool hasPrimaryAudio,
        out int extraAudioStartIndex)
    {
        // GPU backend detection for GPU scale filters
        var gpuBackend = FFmpegCommandComposer.DetectedGpuBackend;
        var useGpu = gpuBackend != FFmpegCommandComposer.GpuFilterBackend.None;

        // GPU hardware device init
        if (useGpu)
        {
            var (initHwDevice, _, _, _, _) = FFmpegCommandComposer.GetGpuFilterArgs(config.ResolutionWidth, config.ResolutionHeight);
            if (!string.IsNullOrEmpty(initHwDevice))
                args.Append($"{initHwDevice} ");
        }

        // Input 0: black background canvas
        args.Append($"-f lavfi -i \"color=c=black:s={config.ResolutionWidth}x{config.ResolutionHeight}" +
                    $":r={config.FrameRate}:d=86400\" ");

        // Inputs 1..N: visual sources
        var cudaZeroCopy = gpuBackend == FFmpegCommandComposer.GpuFilterBackend.Cuda;
        foreach (var seg in visualSegments)
        {
            if (seg.IsVideo)
            {
                if (cudaZeroCopy)
                    args.Append($"-hwaccel cuda -hwaccel_output_format cuda -i \"{seg.SourcePath}\" ");
                else
                    args.Append($"-hwaccel d3d11va -i \"{seg.SourcePath}\" ");
            }
            else
            {
                var loopDur = (seg.EndTime - seg.StartTime + 0.5).ToString("F3", Inv);
                args.Append($"-loop 1 -t {loopDur} -i \"{seg.SourcePath}\" ");
            }
        }

        // Input N+1: primary audio
        hasPrimaryAudio = !string.IsNullOrWhiteSpace(config.AudioPath) && File.Exists(config.AudioPath);
        primaryAudioIndex = visualSegments.Count + 1;
        if (hasPrimaryAudio)
            args.Append($"-i \"{config.AudioPath}\" ");
        else
            args.Append($"-f lavfi -i \"anullsrc=r=44100:cl=stereo\" ");

        // Extra audio inputs
        extraAudioStartIndex = primaryAudioIndex + 1;
        foreach (var aseg in audioSegments)
            args.Append($"-i \"{aseg.SourcePath}\" ");
    }

    // ─── Visual scale filters ──────────────────────────────────────────────

    private static void BuildVisualScaleFilters(
        StringBuilder filter,
        List<RenderVisualSegment> visualSegments,
        RenderConfig config)
    {
        var gpuBackend = FFmpegCommandComposer.DetectedGpuBackend;
        var hasGpu = gpuBackend != FFmpegCommandComposer.GpuFilterBackend.None;
        var cudaZeroCopy = gpuBackend == FFmpegCommandComposer.GpuFilterBackend.Cuda;

        for (int i = 0; i < visualSegments.Count; i++)
        {
            var inputIdx = i + 1;
            var seg = visualSegments[i];
            var duration = (seg.EndTime - seg.StartTime).ToString("F3", Inv);
            var srcOffset = Math.Max(0, seg.SourceOffsetSeconds).ToString("F3", Inv);
            var scaledLabel = $"scaled{i}";

            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var needsAlpha = isPngOverlay || seg.HasAlpha;

            if (seg.IsVideo && hasGpu && !needsAlpha)
            {
                var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                    ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                    : (config.ResolutionWidth, config.ResolutionHeight);
                var gpuScaleExact = FFmpegCommandComposer.BuildGpuScaleExact(scaleW, scaleH);

                filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                if (cudaZeroCopy)
                    filter.Append($"{gpuScaleExact},hwdownload,format=yuv420p,setsar=1");
                else
                    filter.Append($"format=yuv420p,{FFmpegCommandComposer.GpuHwuploadFilter()},{gpuScaleExact},hwdownload,format=yuv420p,setsar=1");
            }
            else if (seg.IsVideo)
            {
                // CPU fallback for video (alpha-needed or no GPU)
                var pixFmt = needsAlpha ? "rgba" : "yuv420p";
                var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                    ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                    : BuildScalingFilter(config);
                filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                filter.Append($"format={pixFmt},{scaleFilter},setsar=1");
            }
            else
            {
                // Image/PNG input (added as -i with -loop 1 -t)
                var pixFmt = needsAlpha ? "rgba" : "yuv420p";
                var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                    ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                    : BuildScalingFilter(config);

                var zoompanFilter = MotionFilterBuilder.BuildZoompanFilter(seg, config.FrameRate,
                    config.ResolutionWidth, config.ResolutionHeight);

                if (zoompanFilter != null)
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{zoompanFilter},setsar=1");
                else
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{scaleFilter},setsar=1");
            }

            // Apply color overlay tint (darken/tint effect) when opacity > 0
            if (seg.OverlayOpacity > 0 && !string.IsNullOrWhiteSpace(seg.OverlayColorHex))
            {
                var hex = seg.OverlayColorHex.TrimStart('#');
                var alpha = seg.OverlayOpacity.ToString("F2", Inv);
                filter.Append($",drawbox=x=0:y=0:w=iw:h=ih:color=0x{hex}@{alpha}:t=fill");
            }

            filter.Append($"[{scaledLabel}];");
        }
    }

    // ─── Visual overlay chain ──────────────────────────────────────────────

    private static string BuildVisualOverlayChain(
        StringBuilder filter,
        List<RenderVisualSegment> visualSegments)
    {
        var currentVideo = "base";
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var seg = visualSegments[i];
            var start = seg.StartTime.ToString("F3", Inv);
            var end = seg.EndTime.ToString("F3", Inv);
            var outLabel = $"v{i}";

            var overlayX = seg.OverlayX ?? "0";
            var overlayY = seg.OverlayY ?? "0";
            var overlayPos = $"x={overlayX}:y={overlayY}:";

            var shiftedLabel = seg.IsVideo ? $"shifted{i}" : $"scaledpts{i}";
            filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{shiftedLabel}];");
            filter.Append($"[{currentVideo}][{shiftedLabel}]overlay={overlayPos}" +
                          $"format=auto:shortest=0:eof_action=pass:enable='between(t,{start},{end})'[{outLabel}];");

            currentVideo = outLabel;
        }
        return currentVideo;
    }

    // ─── Text overlays ─────────────────────────────────────────────────────

    private static string BuildTextOverlays(
        StringBuilder filter,
        List<RenderTextSegment> textSegments,
        string currentVideo)
    {
        if (textSegments.Count == 0)
            return currentVideo;

        string? resolvedDefaultFont = FFmpegCodecHelper.ResolveDefaultFontPath();

        var textTempDir = Path.Combine(Path.GetTempPath(), "pve", "rt");
        Directory.CreateDirectory(textTempDir);

        for (int i = 0; i < textSegments.Count; i++)
        {
            var ts = textSegments[i];
            var start = ts.StartTime.ToString("F3", Inv);
            var end = ts.EndTime.ToString("F3", Inv);
            var outLabel = $"t{i}";

            var textFilePath = Path.Combine(textTempDir, $"seg_{i}.txt");
            File.WriteAllText(textFilePath, ts.Text, new UTF8Encoding(false));

            var fontPath = ts.FontFilePath;
            if (string.IsNullOrWhiteSpace(fontPath))
                fontPath = resolvedDefaultFont;

            var fontfileArg = string.IsNullOrWhiteSpace(fontPath)
                ? string.Empty
                : $"fontfile='{FFmpegCodecHelper.EscapeFilterPath(fontPath)}':";

            var escapedTextFilePath = FFmpegCodecHelper.EscapeFilterPath(textFilePath);

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

        return currentVideo;
    }

    // ─── Audio mixing ──────────────────────────────────────────────────────

    private static string BuildAudioMix(
        StringBuilder filter,
        List<RenderAudioSegment> audioSegments,
        int primaryAudioIndex,
        double primaryAudioVolume)
    {
        if (audioSegments.Count == 0)
        {
            var primVol = primaryAudioVolume.ToString("F3", Inv);
            filter.Append($"[{primaryAudioIndex}:a]volume={primVol}[amain];");
            return "amain";
        }

        var primVolStr = primaryAudioVolume.ToString("F3", Inv);
        filter.Append($"[{primaryAudioIndex}:a]volume={primVolStr}," +
                      "aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[amain];");

        var extraAudioStartIndex = primaryAudioIndex + 1;
        var mixLabels = new List<string> { "amain" };

        for (int i = 0; i < audioSegments.Count; i++)
        {
            var aseg = audioSegments[i];
            var inputIdx = extraAudioStartIndex + i;
            var delayMs = (long)Math.Round(aseg.StartTime * 1000);
            var duration = aseg.EndTime - aseg.StartTime;
            var srcOffset = Math.Max(0, aseg.SourceOffsetSeconds).ToString("F3", Inv);
            var clipLabel = $"aclip{i}";

            if (aseg.IsLooping)
            {
                filter.Append($"[{inputIdx}:a]aloop=loop=-1:size=2147483647,");
                filter.Append($"atrim=start=0:duration={duration.ToString("F3", Inv)},");
            }
            else
            {
                filter.Append($"[{inputIdx}:a]atrim=start={srcOffset}:duration={duration.ToString("F3", Inv)},");
            }

            filter.Append("asetpts=PTS-STARTPTS,");
            filter.Append($"volume={aseg.Volume.ToString("F3", Inv)},");
            if (aseg.FadeInDuration > 0)
                filter.Append($"afade=t=in:st=0:d={aseg.FadeInDuration.ToString("F3", Inv)},");
            if (aseg.FadeOutDuration > 0)
                filter.Append($"afade=t=out:st={(duration - aseg.FadeOutDuration).ToString("F3", Inv)}" +
                              $":d={aseg.FadeOutDuration.ToString("F3", Inv)},");
            filter.Append($"adelay={delayMs}|{delayMs},");
            filter.Append($"aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[{clipLabel}];");
            mixLabels.Add(clipLabel);
        }

        var allMixInputs = string.Concat(mixLabels.Select(l => $"[{l}]"));
        filter.Append($"{allMixInputs}amix=inputs={mixLabels.Count}" +
                      ":duration=first:dropout_transition=0:normalize=0[amixed]");
        return "amixed";
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static List<RenderVisualSegment> FilterAndSortVisualSegments(RenderConfig config)
    {
        return (config.VisualSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .OrderBy(s => s.ZOrder)
            .ThenBy(s => s.StartTime)
            .ToList();
    }

    private static List<RenderTextSegment> FilterAndSortTextSegments(RenderConfig config)
    {
        return (config.TextSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.EndTime > s.StartTime)
            .OrderBy(s => s.StartTime)
            .ToList();
    }

    private static List<RenderAudioSegment> FilterAndSortAudioSegments(RenderConfig config)
    {
        return (config.AudioSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .OrderBy(s => s.StartTime)
            .ToList();
    }

    private static string BuildScalingFilter(RenderConfig config)
    {
        var mode = (config.ScaleMode ?? "Fill").ToUpperInvariant();
        return mode switch
        {
            "FIT" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=decrease," +
                     $"pad={config.ResolutionWidth}:{config.ResolutionHeight}:(ow-iw)/2:(oh-ih)/2:color=black",
            "STRETCH" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}",
            _ => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=increase," +
                 $"crop={config.ResolutionWidth}:{config.ResolutionHeight}"
        };
    }

    private static string BuildLegacySingleImageCommand(
        RenderConfig config, int crf, string videoCodec, string audioCodec)
    {
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

    private static string WriteFilterScript(string filterStr, string outputPath)
    {
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "pve", "fc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new UTF8Encoding(false));
        Log.Debug("Filter script written to: {Path}\n{Content}", filterScriptPath, filterStr);
        return filterScriptPath;
    }
}
#pragma warning restore CS0618

/// <summary>
/// Helper methods for FFmpeg codec mapping and path escaping.
/// Extracted from FFmpegService for reuse by FFmpegFilterGraphBuilder.
/// </summary>
public static class FFmpegCodecHelper
{
    public static string MapVideoCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "libx264";

        return codec.Trim().ToLowerInvariant() switch
        {
            "h264" or "x264" => "libx264",
            "h265" or "hevc" or "x265" => "libx265",
            "h264_nvenc" => "h264_nvenc",
            "hevc_nvenc" => "hevc_nvenc",
            "h264_qsv" => "h264_qsv",
            "hevc_qsv" => "hevc_qsv",
            "h264_amf" => "h264_amf",
            "hevc_amf" => "hevc_amf",
            _ => codec
        };
    }

    public static string MapAudioCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "aac";

        return codec.Trim().ToLowerInvariant() switch
        {
            "mp3" => "libmp3lame",
            _ => codec
        };
    }

    public static string EscapeFilterPath(string path)
    {
        return path
            .Replace("\\", "/")
            .Replace("'", "\\'")
            .Replace(":", "\\:");
    }

    public static string? ResolveDefaultFontPath()
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
