#nullable enable
#pragma warning disable CS0618 // RenderConfig.TextSegments is intentionally kept for legacy compatibility paths.
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
using System.Runtime.InteropServices;

namespace PodcastVideoEditor.Core.Services;

public static class FFmpegCommandComposer
{
    public enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel
    }

    public enum GpuFilterBackend
    {
        None,
        Cuda,
        Qsv,
        Vulkan,
        OpenCL
    }

    private static readonly object _encoderProbeLock = new();
    private static string? _preferredH264Encoder;
    private static string? _preferredHevcEncoder;
    private static GpuFilterBackend _gpuFilterBackend;
    private static bool _gpuFilterProbed;

    internal static GpuFilterBackend DetectedGpuBackend => _gpuFilterBackend;

    public static (string initHwDevice, string filterHwDevice, string gpuHwupload, string gpuScale, string gpuOverlay) GetGpuFilterArgs(int width, int height) => _gpuFilterBackend switch
    {
        GpuFilterBackend.Cuda => (
            "-init_hw_device cuda=gpudev -filter_hw_device gpudev",
            "gpudev",
            "hwupload_cuda",
            $"scale_cuda={width}:{height}",
            "overlay_cuda"),
        GpuFilterBackend.Qsv => (
            "-init_hw_device qsv=gpudev -filter_hw_device gpudev",
            "gpudev",
            "hwupload=extra_hw_frames=64",
            $"scale_qsv=w={width}:h={height}",
            "overlay_qsv"),
        GpuFilterBackend.Vulkan => (
            "-init_hw_device vulkan=gpudev -filter_hw_device gpudev",
            "gpudev",
            "hwupload",
            $"scale_vulkan=w={width}:h={height}",
            "overlay_vulkan"),
        GpuFilterBackend.OpenCL => (
            "-init_hw_device opencl=gpudev -filter_hw_device gpudev",
            "gpudev",
            "hwupload",
            $"scale_opencl=w={width}:h={height}",
            "overlay_opencl"),
        _ => ("", "", "", "", "")
    };

    internal static string GpuHwuploadFilter() => _gpuFilterBackend switch
    {
        GpuFilterBackend.Cuda => "hwupload_cuda",
        GpuFilterBackend.Qsv => "hwupload=extra_hw_frames=64",
        GpuFilterBackend.Vulkan => "hwupload",
        GpuFilterBackend.OpenCL => "hwupload",
        _ => ""
    };

    internal static string BuildGpuScaleExact(int width, int height) => _gpuFilterBackend switch
    {
        GpuFilterBackend.Cuda => $"scale_cuda={width}:{height}",
        GpuFilterBackend.Qsv => $"scale_qsv=w={width}:h={height}",
        GpuFilterBackend.Vulkan => $"scale_vulkan=w={width}:h={height}",
        GpuFilterBackend.OpenCL => $"scale_opencl=w={width}:h={height}",
        _ => $"scale={width}:{height}"
    };

    /// <summary>
    /// Snapshot of detected GPU capabilities for display in the render UI.
    /// </summary>
    public record GpuCapabilities(
        string H264Encoder,
        string HevcEncoder,
        GpuFilterBackend FilterBackend)
    {
        /// <summary>True when video encoding runs on GPU (NVENC / QSV / AMF — either H264 or HEVC).</summary>
        public bool IsGpuEncoding =>
            H264Encoder is "h264_nvenc" or "h264_qsv" or "h264_amf" ||
            HevcEncoder is "hevc_nvenc" or "hevc_qsv" or "hevc_amf";

        /// <summary>True when compositing (scale + overlay) runs on GPU.</summary>
        /// <summary>
        /// True when GPU compositing (scale + overlay) is ACTUALLY used during rendering.
        /// Currently always false because overlay_cuda/overlay_qsv do not support
        /// timeline 'enable' expressions required for segment timing.
        /// The detected FilterBackend is still probed and stored so it can be
        /// re-enabled in the future when FFmpeg adds timeline support.
        /// </summary>
        public bool IsGpuFiltering => false; // FilterBackend != GpuFilterBackend.None — re-enable when useCudaOverlay is enabled

        private string EncoderLabel => H264Encoder switch
        {
            "h264_amf"  => "AMD AMF",
            "h264_nvenc" => "NVENC",
            "h264_qsv"  => "Intel QSV",
            _ => H264Encoder
        };

        /// <summary>Human-readable status line shown in the render panel.</summary>
        public string StatusText => (IsGpuEncoding, IsGpuFiltering) switch
        {
            (true,  true)  => $"GPU full pipeline ({EncoderLabel} + {FilterBackend}): decode + composite + encode ✔",
            (true,  false) => $"GPU encode ({EncoderLabel}) + auto decode — CPU composite",
            (false, true)  => $"GPU composite ({FilterBackend}) + CPU encode",
            _              => "CPU encode + composite — no GPU acceleration detected",
        };

        /// <summary>Hex colour for the status indicator dot / text.</summary>
        public string StatusColor => IsGpuEncoding ? "#28A745" : IsGpuFiltering ? "#FFA500" : "#888888";
    }

    /// <summary>
    /// Returns the GPU capabilities detected at startup (encoder + filter backend).
    /// Triggers the lazy probe on first call; cheap on subsequent calls (cached).
    /// </summary>
    public static GpuCapabilities GetGpuCapabilities()
    {
        EnsurePreferredEncodersInitialized();
        return new GpuCapabilities(
            _preferredH264Encoder ?? "libx264",
            _preferredHevcEncoder ?? "libx265",
            _gpuFilterBackend);
    }

    /// <summary>
    /// Invalidate the encoder / GPU-filter cache so the next call to
    /// <see cref="GetGpuCapabilities"/> (or any build method) re-probes from
    /// scratch using the currently active FFmpeg binary.
    /// Called by <see cref="FFmpegUpdateService"/> after redirecting to a
    /// compatible FFmpeg build.
    /// </summary>
    public static void InvalidateEncoderCache()
    {
        lock (_encoderProbeLock)
        {
            _preferredH264Encoder = null;
            _preferredHevcEncoder = null;
            _gpuFilterBackend = GpuFilterBackend.None;
            _gpuFilterProbed = false;
        }
        Log.Information("FFmpegCommandComposer: Encoder/filter cache invalidated – will re-probe on next use.");
    }

    /// <summary>
    /// Normalize and build the complete FFmpeg argument string for a render config.
    /// Entry point used by <see cref="FFmpegService.RenderVideoAsync"/>.
    /// </summary>
    public static (string args, RenderConfig normalizedConfig) Build(RenderConfig config)
    {
        var normalized = NormalizeRenderConfig(config);
        var args = BuildFFmpegCommand(StageRenderInputsIfNeeded(normalized));
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
        var qualityArgs = BuildQualityArgs(videoCodec, crf, config.ResolutionWidth, config.ResolutionHeight, config.FrameRate, motionComplexity: 0.0);
        var encPreset = GetAdaptiveEncoderPreset(videoCodec, config.Quality, motionComplexity: 0.0);
        var presetArg = !string.IsNullOrEmpty(encPreset) ? $"-preset {encPreset} " : "";

        var logicalCores = Environment.ProcessorCount;
        var renderThreads = Math.Max(2, logicalCores - 2);

        return $"-loop 1 " +
               $"-i \"{config.ImagePath}\" " +
               $"-i \"{config.AudioPath}\" " +
               $"-c:v {videoCodec} " +
               $"{qualityArgs}" +
               $"{presetArg}" +
               $"-vf \"{scaleFilter},setsar=1\" " +
               "-pix_fmt yuv420p " +
               $"-r {config.FrameRate} " +
               $"-c:a {audioCodec} " +
               $"-threads {renderThreads} " +
               "-movflags +faststart " +
               $"-shortest " +
               $"-y " +
               $"\"{config.OutputPath}\"";
    }

    private static string BuildTimelineFFmpegCommand(RenderConfig config)
    {
        // ── Try concat pipeline first (dramatically faster for sequential segments) ──
        var concatResult = TryBuildConcatPipeline(config);
        if (concatResult != null)
            return concatResult;

        var crf = config.GetCrfValue();
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var invariant = CultureInfo.InvariantCulture;

        // Detect GPU backend once
        var gpuBackend = DetectedGpuBackend;

        // ── GPU compositing policy ─────────────────────────────────────
        // overlay_cuda does not support the 'enable' timeline option in current
        // FFmpeg builds, so segment start/end timing is unreliable with it.
        // When overlay runs on CPU, CUDA filter init wastes VRAM (causes
        // CUDA_ERROR_OUT_OF_MEMORY on consumer GPUs) and GPU scale + hwdownload
        // is slower than CPU scale due to PCIe round-trip overhead.
        //
        // Strategy: GPU decode (auto) + GPU encode (nvenc) + CPU composite (default).
        // GPU overlay can be enabled for simple timelines or chunked renders via
        // config.UseGpuOverlay=true, but segment timing may be unreliable.
        var useCudaOverlay = config.UseGpuOverlay && gpuBackend == GpuFilterBackend.Cuda;
        if (useCudaOverlay)
            Log.Debug("GPU overlay enabled for compositing (requires simple timeline without enable expressions)");
        // GPU decode is independent of filter backend. D3D11VA works on virtually
        // ALL Windows 10+ GPUs (NVIDIA/AMD/Intel, even integrated). Using -hwaccel
        // auto lets FFmpeg try d3d11va → dxva2 → software, ensuring the best
        // decode path on every machine without hard failures.
        var useGpuDecode = true;

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

        var useEmbeddedSources = !config.DisableEmbeddedTimelineSources &&
                     ShouldUseEmbeddedTimelineSources(visualSegments, audioSegments);
        var preferFastCpuScale = visualSegments.Count >= 120;
        if (preferFastCpuScale)
        {
            Log.Information("Large timeline detected ({Count} visual segments) — enabling fast CPU scale flags", visualSegments.Count);
        }

        if (visualSegments.Count == 0 && textSegments.Count == 0 && audioSegments.Count == 0)
            return BuildLegacySingleImageCommand(config, crf);

        var args = new StringBuilder();

        // ── GPU hardware device init ──
        // Only needed when GPU compositing (overlay_cuda) is active.
        // When useCudaOverlay=false, d3d11va decode + nvenc encode work without
        // a CUDA filter context, avoiding VRAM exhaustion.
        if (useGpuDecode && useCudaOverlay)
        {
            var (initHwDevice, _, _, _, _) = GetGpuFilterArgs(config.ResolutionWidth, config.ResolutionHeight);
            if (!string.IsNullOrEmpty(initHwDevice))
                args.Append($"{initHwDevice} ");
        }

        // ── Input 0: black background canvas (duration = max segment end + 1s safety margin)
        var maxEndTime = new[]
        {
            visualSegments.Count > 0 ? visualSegments.Max(s => s.EndTime) : 0,
            audioSegments.Count > 0 ? audioSegments.Max(s => s.EndTime) : 0,
        }.Max();
        if (maxEndTime <= 0) maxEndTime = 10;
        var canvasDuration = (int)Math.Ceiling(maxEndTime) + 1;
        args.Append($"-f lavfi -i \"color=c=black:s={config.ResolutionWidth}x{config.ResolutionHeight}:r={config.FrameRate}:d={canvasDuration}\" ");

        // ── Inputs 1..N : visual sources (deduplicated by source path)
        // -thread_queue_size 512: prevents buffer underrun when many inputs compete
        // for decode bandwidth. Default (8) is too small for 10+ concurrent inputs.
        var cudaZeroCopy = useCudaOverlay && gpuBackend == GpuFilterBackend.Cuda;

        var visualSourceRefByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visualUniqueInputs = new List<(string SourcePath, bool IsVideo, string SourceRef)>();
        var visualSegmentSourceRefs = new string[visualSegments.Count];
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var seg = visualSegments[i];
            var key = BuildMediaInputKey(seg.SourcePath, seg.IsVideo);
            if (!visualSourceRefByKey.TryGetValue(key, out var sourceRef))
            {
                sourceRef = useEmbeddedSources
                    ? $"vsrc{visualUniqueInputs.Count}"
                    : $"{visualUniqueInputs.Count + 1}:v"; // input 0 is canvas
                visualSourceRefByKey[key] = sourceRef;
                visualUniqueInputs.Add((seg.SourcePath, seg.IsVideo, sourceRef));
            }

            visualSegmentSourceRefs[i] = sourceRef;
        }

        if (!useEmbeddedSources)
        {
            foreach (var input in visualUniqueInputs)
            {
                if (input.IsVideo)
                {
                    if (cudaZeroCopy)
                    {
                        // CUDA zero-copy: decode on GPU, keep frames in VRAM.
                        args.Append($"-thread_queue_size 512 -hwaccel cuda -hwaccel_output_format cuda -i \"{input.SourcePath}\" ");
                    }
                    else
                    {
                        // -hwaccel auto: FFmpeg tries d3d11va → dxva2 → software.
                        // Safer than hard-coding d3d11va — graceful fallback for
                        // unsupported codecs (VP9/AV1 on older GPUs) or Windows 7.
                        args.Append($"-thread_queue_size 512 -hwaccel auto -i \"{input.SourcePath}\" ");
                    }
                }
                else
                {
                    // Infinite loop image input can be reused for many timeline segments of the same source.
                    args.Append($"-thread_queue_size 512 -loop 1 -i \"{input.SourcePath}\" ");
                }
            }
        }

        Log.Debug("Timeline input dedup: visual {SegCount} → {UniqueCount} unique sources",
            visualSegments.Count, visualUniqueInputs.Count);

        // ── Primary audio or silent placeholder
        var hasPrimaryAudio = !string.IsNullOrWhiteSpace(config.AudioPath) && File.Exists(config.AudioPath);
        var primaryAudioIndex = useEmbeddedSources ? 1 : visualUniqueInputs.Count + 1;
        if (hasPrimaryAudio)
            args.Append($"-i \"{config.AudioPath}\" ");
        else
            args.Append($"-f lavfi -i \"anullsrc=r=44100:cl=stereo\" ");

        // ── Extra audio clip inputs (deduplicated by source path)
        var audioSourceRefByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var audioUniqueInputs = new List<(string SourcePath, string SourceRef)>();
        var audioSegmentSourceRefs = new string[audioSegments.Count];
        for (int i = 0; i < audioSegments.Count; i++)
        {
            var aseg = audioSegments[i];
            var key = NormalizePathForKey(aseg.SourcePath);
            if (!audioSourceRefByKey.TryGetValue(key, out var sourceRef))
            {
                sourceRef = useEmbeddedSources
                    ? $"asrc{audioUniqueInputs.Count}"
                    : $"{primaryAudioIndex + 1 + audioUniqueInputs.Count}:a";
                audioSourceRefByKey[key] = sourceRef;
                audioUniqueInputs.Add((aseg.SourcePath, sourceRef));
            }

            audioSegmentSourceRefs[i] = sourceRef;
        }

        if (!useEmbeddedSources)
        {
            foreach (var audioInput in audioUniqueInputs)
                args.Append($"-i \"{audioInput.SourcePath}\" ");
        }

        if (audioSegments.Count > 0)
        {
            Log.Debug("Timeline input dedup: audio {SegCount} → {UniqueCount} unique sources",
                audioSegments.Count, audioUniqueInputs.Count);
        }

        if (useEmbeddedSources)
        {
            Log.Debug("Using embedded movie/amovie sources for large timeline fallback");
        }

        // ── filter_complex
        var filter = new StringBuilder();
        var (_, _, gpuHwupload, gpuScale, gpuOverlay) = GetGpuFilterArgs(config.ResolutionWidth, config.ResolutionHeight);
        var hasGpuFilters = useCudaOverlay && gpuBackend != GpuFilterBackend.None;

        // Log GPU pipeline diagnostics
        var isGpuEncoder = videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
                           videoCodec.Contains("qsv",   StringComparison.OrdinalIgnoreCase) ||
                           videoCodec.Contains("amf",   StringComparison.OrdinalIgnoreCase);
        if (hasGpuFilters && cudaZeroCopy)
            Log.Debug("GPU: CUDA full-GPU pipeline (scale_cuda + overlay_cuda), encoder {Encoder}", videoCodec);
        else if (hasGpuFilters)
            Log.Debug("GPU: {Backend} for scale + CPU overlay, encoder {Encoder}", gpuBackend, videoCodec);
        else if (isGpuEncoder)
            Log.Debug("GPU: encode ({Encoder}) + auto decode, CPU composite", videoCodec);
        else
            Log.Debug("GPU: CPU-only pipeline (no GPU), encoder {Encoder}", videoCodec);

        // useCudaOverlay policy is declared above (before input declarations).

        // Step 1: base canvas
        if (useCudaOverlay)
            filter.Append($"[0:v]format=yuv420p,hwupload_cuda,setsar=1[base];");
        else
            filter.Append($"[0:v]format=yuv420p,setsar=1[base];");

        if (useEmbeddedSources)
        {
            foreach (var input in visualUniqueInputs)
            {
                var escapedPath = EscapeFilterPath(input.SourcePath);
                if (input.IsVideo)
                    filter.Append($"movie=filename='{escapedPath}'[{input.SourceRef}];");
                else
                    filter.Append($"movie=filename='{escapedPath}',loop=loop=-1:size=1:start=0,fps={config.FrameRate}[{input.SourceRef}];");
            }

            foreach (var audioInput in audioUniqueInputs)
            {
                var escapedPath = EscapeFilterPath(audioInput.SourcePath);
                filter.Append($"amovie=filename='{escapedPath}'[{audioInput.SourceRef}];");
            }
        }

        // Track whether each scaled segment is a CUDA surface (true) or CPU frame (false).
        var segOnGpu = new bool[visualSegments.Count];

        // Step 2: Scale + position each visual segment
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var sourceRef = visualSegmentSourceRefs[i];
            var seg = visualSegments[i];
            var start    = seg.StartTime.ToString("F3", invariant);
            var end      = seg.EndTime.ToString("F3", invariant);
            var duration = (seg.EndTime - seg.StartTime).ToString("F3", invariant);
            var srcOffset = Math.Max(0, seg.SourceOffsetSeconds).ToString("F3", invariant);
            var scaledLabel = $"scaled{i}";

            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var needsAlpha = isPngOverlay || seg.HasAlpha;

            // Build optional fade-in/fade-out filter for this segment
            var fadeFilter = BuildFadeFilter(seg, invariant);
            var hasFade = !string.IsNullOrEmpty(fadeFilter);
            var overlayTintFilter = BuildOverlayTintFilter(seg, invariant);
            var hasOverlayTint = !string.IsNullOrEmpty(overlayTintFilter);

            // Tint in RGB space to avoid YUV chroma washout (gray-looking output).
            // When tint is active, force RGBA processing for this segment chain.
            var segmentPixFmt = hasOverlayTint ? "rgba" : null;

            // Segments that MUST stay on CPU (alpha blending, zoompan, fade, tint overlay)
            var forceCpu = needsAlpha || hasFade || hasOverlayTint;

            if (seg.IsVideo)
            {
                if (useCudaOverlay && !forceCpu)
                {
                    // CUDA path: keep as CUDA surface for overlay_cuda.
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);

                    filter.Append($"[{sourceRef}]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                    if (cudaZeroCopy)
                    {
                        // Frames are already CUDA surfaces from -hwaccel_output_format cuda.
                        filter.Append($"{gpuScaleExact},setsar=1[{scaledLabel}];");
                    }
                    else
                    {
                        filter.Append($"format=yuv420p,hwupload_cuda,{gpuScaleExact},setsar=1[{scaledLabel}];");
                    }
                    segOnGpu[i] = true;
                }
                else if (hasGpuFilters && !needsAlpha)
                {
                    // GPU scale but download to CPU (has fade or non-CUDA backend).
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);

                    filter.Append($"[{sourceRef}]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                    if (cudaZeroCopy)
                        filter.Append($"{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                    else
                        filter.Append($"format=yuv420p,{GpuHwuploadFilter()},{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                }
                else
                {
                    // CPU fallback (alpha-needed or no GPU backend)
                    var pixFmt = segmentPixFmt ?? (needsAlpha ? "rgba" : "yuv420p");
                    var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                        ? BuildScaleFilter(seg.ScaleWidth.Value, seg.ScaleHeight.Value, seg.ScaleMode, preferFastCpuScale)
                        : BuildScalingFilter(config, preferFastCpuScale);
                    filter.Append($"[{sourceRef}]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                    filter.Append($"format={pixFmt},{scaleFilter},setsar=1{overlayTintFilter}{fadeFilter}[{scaledLabel}];");
                }
            }
            else
            {
                // ── Image segment: use standard -i input ──
                var pixFmt = segmentPixFmt ?? (needsAlpha ? "rgba" : "yuv420p");
                var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                    ? BuildScaleFilter(seg.ScaleWidth.Value, seg.ScaleHeight.Value, seg.ScaleMode, preferFastCpuScale)
                    : BuildScalingFilter(config, preferFastCpuScale);

                // Check for Ken Burns motion effect (zoom/pan) — CPU only
                var zoompanFilter = MotionFilterBuilder.BuildZoompanFilter(seg, config.FrameRate,
                    config.ResolutionWidth, config.ResolutionHeight, pixFmt);

                if (zoompanFilter != null)
                {
                    var estimatedInputFrames = Math.Max(1, (int)Math.Ceiling((seg.EndTime - seg.StartTime) * config.FrameRate));
                    var motionFrames = Math.Max(1, MotionEngine.ComputeTotalFrames(seg.Duration, config.FrameRate));
                    var estimatedFanout = estimatedInputFrames * motionFrames;
                    if (estimatedInputFrames > 1)
                    {
                        Log.Debug(
                            "Motion segment {Index} uses still-image zoompan with {InputFrames} input frames and d={MotionFrames} (est. fan-out {Fanout}); forcing single-frame feed to avoid frame explosion and jitter.",
                            i,
                            estimatedInputFrames,
                            motionFrames,
                            estimatedFanout);
                    }

                    filter.Append($"[{sourceRef}]trim=duration={duration},setpts=PTS-STARTPTS,select='eq(n,0)',setpts=PTS-STARTPTS,format={pixFmt},{zoompanFilter},setsar=1{overlayTintFilter}{fadeFilter}[{scaledLabel}];");
                }
                else if (useCudaOverlay && !forceCpu)
                {
                    // GPU scale for non-alpha images, keep as CUDA surface.
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);
                    filter.Append($"[{sourceRef}]trim=duration={duration},setpts=PTS-STARTPTS,");
                    filter.Append($"format=yuv420p,hwupload_cuda,{gpuScaleExact},setsar=1[{scaledLabel}];");
                    segOnGpu[i] = true;
                }
                else if (hasGpuFilters && !needsAlpha)
                {
                    // GPU scale but download to CPU (non-CUDA backend or has fade).
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);
                    filter.Append($"[{sourceRef}]trim=duration={duration},setpts=PTS-STARTPTS,");
                    filter.Append($"format=yuv420p,{GpuHwuploadFilter()},{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                }
                else
                {
                    filter.Append($"[{sourceRef}]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{scaleFilter},setsar=1{overlayTintFilter}{fadeFilter}[{scaledLabel}];");
                }
            }
        }

        // Step 3: Overlay chain with GPU-state tracking.
        // currentOnGpu tracks whether the current composite is a CUDA surface.
        // When both the composite and the overlay segment are CUDA → overlay_cuda.
        // When transitioning GPU↔CPU, insert hwdownload or hwupload_cuda as needed.
        var currentVideo = "base";
        var currentOnGpu = useCudaOverlay; // base was uploaded to CUDA above
        for (int i = 0; i < visualSegments.Count; i++)
        {
            var seg     = visualSegments[i];
            var start   = seg.StartTime.ToString("F3", invariant);
            var end     = seg.EndTime.ToString("F3", invariant);
            var outLabel = $"v{i}";

            var overlayX = seg.OverlayX ?? "0";
            var overlayY = seg.OverlayY ?? "0";

            var shiftedLabel = seg.IsVideo ? $"shifted{i}" : $"scaledpts{i}";
            filter.Append($"[scaled{i}]setpts=PTS+{start}/TB[{shiftedLabel}];");

            if (currentOnGpu && segOnGpu[i])
            {
                // Both on CUDA → overlay_cuda (fully in VRAM, no PCIe transfer)
                // NOTE: overlay_cuda does NOT support the timeline 'enable' option.
                // Timing is handled correctly by trim+setpts (top starts at {start})
                // and eof_action=pass:repeatlast=0 (base passes through after top ends).
                filter.Append($"[{currentVideo}][{shiftedLabel}]overlay_cuda=x={overlayX}:y={overlayY}:" +
                              $"eof_action=pass:repeatlast=0[{outLabel}];");
                // result remains on GPU
            }
            else
            {
                // Need CPU overlay (alpha segment, or mixed GPU/CPU state).
                // Ensure both inputs are on CPU.
                if (currentOnGpu)
                {
                    // Download current composite from GPU to CPU.
                    var dlLabel = $"dl{i}";
                    filter.Append($"[{currentVideo}]hwdownload,format=yuv420p[{dlLabel}];");
                    currentVideo = dlLabel;
                    currentOnGpu = false;
                }
                // segOnGpu[i] shouldn't be true here (alpha/fade segments stay on CPU),
                // but guard anyway.
                if (segOnGpu[i])
                {
                    var dlSeg = $"dlseg{i}";
                    filter.Append($"[{shiftedLabel}]hwdownload,format=yuv420p[{dlSeg}];");
                    shiftedLabel = dlSeg;
                }

                filter.Append($"[{currentVideo}][{shiftedLabel}]overlay=x={overlayX}:y={overlayY}:" +
                              $"format=auto:shortest=0:repeatlast=0:eof_action=pass:" +
                              $"enable='between(t,{start},{end})'[{outLabel}];");

                // After CPU overlay, upload back to CUDA if we're in CUDA pipeline
                // and the NEXT segment is also GPU-eligible.
                if (useCudaOverlay && i + 1 < visualSegments.Count && segOnGpu.AsSpan()[(i + 1)..].Contains(true))
                {
                    var upLabel = $"up{i}";
                    filter.Append($"[{outLabel}]format=yuv420p,hwupload_cuda[{upLabel}];");
                    outLabel = upLabel;
                    currentOnGpu = true;
                }
            }
            currentVideo = outLabel;
        }

        // Before text overlays (drawtext is CPU-only), download from GPU if needed.
        if (currentOnGpu)
        {
            var dlFinal = "dlfinal";
            filter.Append($"[{currentVideo}]hwdownload,format=yuv420p[{dlFinal}];");
            currentVideo = dlFinal;
            currentOnGpu = false;
        }

        // Resolve a default font once for all text segments
        string? resolvedDefaultFont = ResolveDefaultFontPath();

        // Step 4: Text overlays via drawtext
        var textTempDir = Path.Combine(Path.GetTempPath(), "pve", "rt");
        if (textSegments.Count > 0)
            Directory.CreateDirectory(textTempDir);

        for (int i = 0; i < textSegments.Count; i++)
        {
            var ts = textSegments[i];
            var start = ts.StartTime.ToString("F3", invariant);
            var end   = ts.EndTime.ToString("F3", invariant);
            var outLabel = $"t{i}";

            var textFilePath = Path.Combine(textTempDir, $"s{i}.txt");
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
                var audioSourceRef = audioSegmentSourceRefs[i];
                var delayMs     = (long)Math.Round(aseg.StartTime * 1000);
                var duration    = aseg.EndTime - aseg.StartTime;
                var srcOffset   = Math.Max(0, aseg.SourceOffsetSeconds).ToString("F3", invariant);
                var clipLabel   = $"aclip{i}";

                if (aseg.IsLooping)
                {
                    filter.Append($"[{audioSourceRef}]aloop=loop=-1:size=2147483647,");
                    filter.Append($"atrim=start=0:duration={duration.ToString("F3", invariant)},");
                }
                else
                {
                    filter.Append($"[{audioSourceRef}]atrim=start={srcOffset}:duration={duration.ToString("F3", invariant)},");
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

        // Write filter to a temp script file (short path to save command-line chars)
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "pve", "fc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new UTF8Encoding(false));
        Log.Debug("Filter script written to: {Path} ({Length} chars)", filterScriptPath, filterStr.Length);

        // -filter_complex_script was removed in FFmpeg 7.1; use -/filter_complex to
        // read the graph from a file (FFmpeg itself suggests this in the deprecation msg).
        args.Append($"-/filter_complex \"{filterScriptPath}\" ");

        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");

        args.Append($"-c:v {videoCodec} ");
        var overlayMotionComplexity = ComputeMotionComplexity(visualSegments);
        args.Append(BuildQualityArgs(videoCodec, crf, config.ResolutionWidth, config.ResolutionHeight, config.FrameRate, overlayMotionComplexity));
        var preset = GetAdaptiveEncoderPreset(videoCodec, config.Quality, overlayMotionComplexity);
        if (!string.IsNullOrEmpty(preset))
            args.Append($"-preset {preset} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");
        // Thread management: reserve 2 logical cores for OS/UI responsiveness.
        // Commercial editors (DaVinci, Premiere) cap render threads to ~75% of
        // available cores. Combined with BelowNormal process priority, this
        // ensures the system stays usable during long renders.
        var logicalCores = Environment.ProcessorCount;
        var renderThreads = Math.Max(2, logicalCores - 2);
        var filterThreads = Math.Max(2, logicalCores / 2);
        args.Append($"-threads {renderThreads} ");
        // -filter_threads: parallelize individual filter operations (overlay, scale)
        //   that are normally single-threaded. Significantly reduces wall time when
        //   the filter graph has many overlay chains.
        // -filter_complex_threads: parallelize independent filter graph branches
        //   (e.g. separate visual segment scale+motion operations run concurrently).
        args.Append($"-filter_threads {filterThreads} ");
        args.Append($"-filter_complex_threads {filterThreads} ");
        Log.Debug("Threads: {RenderThreads}/{LogicalCores} cores, filter_threads: {FilterThreads}",
            renderThreads, logicalCores, filterThreads);
        args.Append("-movflags +faststart ");
        // Prevent "Too many packets buffered for output stream" on long timelines
        // with many audio/video inputs. Default (128) is too small for podcasts
        // with 10+ segments and 30+ minute durations.
        args.Append("-max_muxing_queue_size 4096 ");
        // Always add -t flag so FFmpeg stops at the timeline end, not at the end of
        // the (potentially longer) audio file.
        {
            var allEndTimes = visualSegments.Select(s => s.EndTime)
                .Concat(textSegments.Select(s => s.EndTime))
                .Concat(audioSegments.Select(s => s.EndTime));
            var maxEnd = allEndTimes.Any() ? allEndTimes.Max() : 1.0;
            args.Append($"-t {maxEnd.ToString("F3", invariant)} ");
        }
        // NOTE: Do NOT use -shortest here. It conflicts with -t and can cause
        // premature termination when a looped image input "ends" before the audio.
        // The -t flag already precisely controls output duration.
        args.Append("-y ");
        args.Append($"\"{config.OutputPath}\"");

        return args.ToString();
    }

    // ── Concat pipeline ─────────────────────────────────────────────────
    // For podcast-style content where visual segments are sequential (non-overlapping),
    // this pipeline is dramatically faster than the overlay approach.
    //
    // Current overlay pipeline problem:
    //   128 overlays × 8700 frames = 1,113,600 overlay evaluations
    //   Each frame evaluates ALL enable='between(t,...)' even when 126+ are disabled.
    //
    // Concat pipeline:
    //   Each segment → self-contained filter chain (zoompan + fade + text overlay)
    //   All clips → concat → single output stream
    //   ~5,000 overlay evaluations total (only active text overlays per clip)
    //
    // Expected speedup: 2-5× for typical podcasts with 30+ segments.

    /// <summary>
    /// Attempts to build a concat-based pipeline for sequential non-overlapping segments.
    /// Returns null if segments overlap (falls back to the overlay pipeline).
    /// </summary>
    private static string? TryBuildConcatPipeline(RenderConfig config)
    {
        const int TextZOrderThreshold = 10_000; // text tier starts at 10000
        var invariant = CultureInfo.InvariantCulture;

        var allVisual = (config.VisualSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .ToList();

        // Split into visual-tier (images/video) and text-tier (rasterized PNGs)
        var visualTier = allVisual
            .Where(s => s.ZOrder < TextZOrderThreshold)
            .OrderBy(s => s.StartTime)
            .ToList();

        var textTier = allVisual
            .Where(s => s.ZOrder >= TextZOrderThreshold)
            .OrderBy(s => s.StartTime)
            .ToList();

        if (visualTier.Count < 2)
            return null; // not enough segments to benefit from concat

        // Check if visual segments are sequential (non-overlapping)
        // Allow small overlap tolerance for crossfade (segments can overlap by up to 0.1s)
        for (int i = 0; i < visualTier.Count - 1; i++)
        {
            if (visualTier[i].EndTime > visualTier[i + 1].StartTime + 0.1)
            {
                Log.Information("Concat pipeline: segments {A} and {B} overlap ({Overlap:F3}s) — using overlay pipeline",
                    i, i + 1, visualTier[i].EndTime - visualTier[i + 1].StartTime);
                return null;
            }
        }

        // Videos with GPU compositing or complex overlay positions are not suitable for concat
        if (visualTier.Any(s => s.IsVideo && s.HasAlpha))
            return null;

        Log.Information("Concat pipeline: {Visual} visual + {Text} text segments are sequential — using fast concat",
            visualTier.Count, textTier.Count);

        // Match text segments to visual segments by overlapping time range
        var textByVisual = new List<RenderVisualSegment>[visualTier.Count];
        for (int i = 0; i < visualTier.Count; i++)
            textByVisual[i] = [];

        var unmatchedTextCount = 0;
        foreach (var textSeg in textTier)
        {
            var matched = false;
            // Find the visual segment that this text overlaps with
            for (int i = 0; i < visualTier.Count; i++)
            {
                var vs = visualTier[i];
                if (textSeg.StartTime >= vs.StartTime - 0.05 && textSeg.EndTime <= vs.EndTime + 0.05)
                {
                    textByVisual[i].Add(textSeg);
                    matched = true;
                    break;
                }
            }
            if (!matched)
                unmatchedTextCount++;
        }

        if (unmatchedTextCount > 0)
        {
            Log.Information("Concat pipeline: {Count} text segments are not fully contained in any visual segment — using overlay pipeline",
                unmatchedTextCount);
            return null;
        }

        var audioSegments = (config.AudioSegments ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) &&
                        File.Exists(s.SourcePath) &&
                        s.EndTime > s.StartTime)
            .OrderBy(s => s.StartTime)
            .ToList();

        var crf = config.GetCrfValue();
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);

        var args = new StringBuilder();
        var filter = new StringBuilder();

        // ── Inputs ──
        // Input 0: base canvas for gap-filling (black frame generator)
        var maxEndTime = new[]
        {
            visualTier.Count > 0 ? visualTier.Max(s => s.EndTime) : 0,
            audioSegments.Count > 0 ? audioSegments.Max(s => s.EndTime) : 0,
        }.Max();
        if (maxEndTime <= 0) maxEndTime = 10;
        var canvasDuration = (int)Math.Ceiling(maxEndTime) + 1;
        args.Append($"-f lavfi -i \"color=c=black:s={config.ResolutionWidth}x{config.ResolutionHeight}:r={config.FrameRate}:d={canvasDuration}\" ");

        // Visual inputs (1..N), deduplicated by source path
        int inputIndex = 1;
        var visualInputMap = new int[visualTier.Count]; // maps visual index -> FFmpeg input index
        var concatVisualInputByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < visualTier.Count; i++)
        {
            var seg = visualTier[i];
            var key = BuildMediaInputKey(seg.SourcePath, seg.IsVideo);
            if (!concatVisualInputByKey.TryGetValue(key, out var mappedIndex))
            {
                mappedIndex = inputIndex;
                concatVisualInputByKey[key] = mappedIndex;
                if (seg.IsVideo)
                    args.Append($"-thread_queue_size 512 -hwaccel auto -i \"{seg.SourcePath}\" ");
                else
                    args.Append($"-thread_queue_size 512 -loop 1 -i \"{seg.SourcePath}\" ");
                inputIndex++;
            }

            visualInputMap[i] = mappedIndex;
        }

        Log.Debug("Concat input dedup: {SegCount} visual → {UniqueCount} unique sources",
            visualTier.Count, concatVisualInputByKey.Count);

        // Text PNG inputs (deduplicated by source path)
        // Note: textTier can contain BOTH rasterized PNG overlays (images, need -loop 1)
        // and visualizer video files (IsVideo=true, should NOT have -loop 1).
        var textInputMap = new Dictionary<RenderVisualSegment, int>();
        var textInputByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var textSeg in textTier)
        {
            var key = NormalizePathForKey(textSeg.SourcePath);
            if (!textInputByKey.TryGetValue(key, out var mappedIndex))
            {
                mappedIndex = inputIndex;
                textInputByKey[key] = mappedIndex;
                
                // Only add -loop 1 for image files (PNGs), not for videos (MOV files)
                if (textSeg.IsVideo)
                    args.Append($"-thread_queue_size 512 -hwaccel auto -i \"{textSeg.SourcePath}\" ");
                else
                    args.Append($"-thread_queue_size 512 -loop 1 -i \"{textSeg.SourcePath}\" ");
                inputIndex++;
            }

            textInputMap[textSeg] = mappedIndex;
        }

        // Primary audio input
        var hasPrimaryAudio = !string.IsNullOrWhiteSpace(config.AudioPath) && File.Exists(config.AudioPath);
        var primaryAudioIndex = inputIndex;
        if (hasPrimaryAudio)
            args.Append($"-i \"{config.AudioPath}\" ");
        else
            args.Append($"-f lavfi -i \"anullsrc=r=44100:cl=stereo\" ");
        inputIndex++;

        // Extra audio clip inputs (deduplicated by source path)
        var concatAudioInputMap = new int[audioSegments.Count];
        var concatAudioInputByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int ai = 0; ai < audioSegments.Count; ai++)
        {
            var aseg = audioSegments[ai];
            var key = NormalizePathForKey(aseg.SourcePath);
            if (!concatAudioInputByKey.TryGetValue(key, out var mappedIndex))
            {
                mappedIndex = inputIndex;
                concatAudioInputByKey[key] = mappedIndex;
                args.Append($"-i \"{aseg.SourcePath}\" ");
                inputIndex++;
            }

            concatAudioInputMap[ai] = mappedIndex;
        }

        // ── Filter graph: per-clip chains + concat ──

        var clipLabels = new List<string>();
        var clipIndex = 0;

        // Check for gaps before first segment
        if (visualTier[0].StartTime > 0.05)
        {
            var gapDur = visualTier[0].StartTime.ToString("F3", invariant);
            var gapLabel = $"gap_pre";
            filter.Append($"[0:v]trim=duration={gapDur},setpts=PTS-STARTPTS,format=yuv420p,setsar=1[{gapLabel}];");
            clipLabels.Add(gapLabel);
        }

        for (int i = 0; i < visualTier.Count; i++)
        {
            var seg = visualTier[i];
            var vInput = visualInputMap[i];
            var duration = (seg.EndTime - seg.StartTime).ToString("F3", invariant);
            var srcOffset = Math.Max(0, seg.SourceOffsetSeconds).ToString("F3", invariant);
            var clipLabel = $"clip{clipIndex}";

            // Build the visual part of this clip
            var fadeFilter = BuildFadeFilter(seg, invariant);
            var overlayTintFilter = BuildOverlayTintFilter(seg, invariant);
            var hasOverlayTint = !string.IsNullOrEmpty(overlayTintFilter);
            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var needsAlpha = isPngOverlay || seg.HasAlpha;
            // Keep tint blend in RGB to preserve saturation.
            var pixFmt = hasOverlayTint || needsAlpha ? "rgba" : "yuv420p";

            if (seg.IsVideo)
            {
                var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                    ? BuildScaleFilter(seg.ScaleWidth.Value, seg.ScaleHeight.Value, seg.ScaleMode)
                    : BuildScalingFilter(config);
                filter.Append($"[{vInput}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                filter.Append($"format={pixFmt},{scaleFilter},setsar=1{overlayTintFilter}{fadeFilter}[vbase{clipIndex}];");
            }
            else
            {
                var zoompanFilter = MotionFilterBuilder.BuildZoompanFilter(seg, config.FrameRate,
                    config.ResolutionWidth, config.ResolutionHeight, pixFmt);

                if (zoompanFilter != null)
                {
                    var estimatedInputFrames = Math.Max(1, (int)Math.Ceiling((seg.EndTime - seg.StartTime) * config.FrameRate));
                    var motionFrames = Math.Max(1, MotionEngine.ComputeTotalFrames(seg.Duration, config.FrameRate));
                    var estimatedFanout = estimatedInputFrames * motionFrames;
                    if (estimatedInputFrames > 1)
                    {
                        Log.Debug(
                            "Concat motion clip {ClipIndex} uses still-image zoompan with {InputFrames} input frames and d={MotionFrames} (est. fan-out {Fanout}); forcing single-frame feed to avoid frame explosion and jitter.",
                            clipIndex,
                            estimatedInputFrames,
                            motionFrames,
                            estimatedFanout);
                    }

                    filter.Append($"[{vInput}:v]trim=duration={duration},setpts=PTS-STARTPTS,select='eq(n,0)',setpts=PTS-STARTPTS,format={pixFmt},{zoompanFilter},setsar=1{overlayTintFilter}{fadeFilter}[vbase{clipIndex}];");
                }
                else
                {
                    var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                        ? BuildScaleFilter(seg.ScaleWidth.Value, seg.ScaleHeight.Value, seg.ScaleMode)
                        : BuildScalingFilter(config);
                    filter.Append($"[{vInput}:v]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{scaleFilter},setsar=1{overlayTintFilter}{fadeFilter}[vbase{clipIndex}];");
                }
            }

            // Overlay text PNGs for this clip (if any)
            var currentClipVideo = $"vbase{clipIndex}";
            var matchedTexts = textByVisual[i];
            for (int t = 0; t < matchedTexts.Count; t++)
            {
                var textSeg = matchedTexts[t];
                if (!textInputMap.TryGetValue(textSeg, out var textInput))
                    continue;

                var textW = textSeg.ScaleWidth ?? config.ResolutionWidth;
                var textH = textSeg.ScaleHeight ?? 80;
                var textDur = (textSeg.EndTime - textSeg.StartTime).ToString("F3", invariant);
                var overlayX = textSeg.OverlayX ?? "0";
                var overlayY = textSeg.OverlayY ?? "0";
                var textOut = $"ct{clipIndex}_{t}";

                // Text start/end relative to the clip's local timeline
                var textRelStart = Math.Max(0, textSeg.StartTime - seg.StartTime).ToString("F3", invariant);
                var textRelEnd = Math.Min(seg.EndTime - seg.StartTime, textSeg.EndTime - seg.StartTime).ToString("F3", invariant);

                filter.Append($"[{textInput}:v]trim=duration={textDur},setpts=PTS-STARTPTS,format=rgba,scale={textW}:{textH},setsar=1[tscaled{clipIndex}_{t}];");
                filter.Append($"[{currentClipVideo}][tscaled{clipIndex}_{t}]overlay=x={overlayX}:y={overlayY}:" +
                              $"format=auto:shortest=0:repeatlast=0:eof_action=pass:" +
                              $"enable='between(t,{textRelStart},{textRelEnd})'[{textOut}];");
                currentClipVideo = textOut;
            }

            // Normalize every clip to the render canvas before concat.
            // This prevents concat failures when a segment has explicit local scale
            // (e.g. 1080x1080) while the project canvas is 1080x1920.
            var clipCanvasLabel = $"clipbg{clipIndex}";
            var overlayXExpr = seg.OverlayX ?? "0";
            var overlayYExpr = seg.OverlayY ?? "0";
            filter.Append($"[0:v]trim=duration={duration},setpts=PTS-STARTPTS,format=yuv420p,setsar=1[{clipCanvasLabel}];");
            filter.Append($"[{clipCanvasLabel}][{currentClipVideo}]overlay=x={overlayXExpr}:y={overlayYExpr}:" +
                          $"format=auto:shortest=0:repeatlast=0:eof_action=pass[{clipLabel}];");
            clipLabels.Add(clipLabel);

            // Check for gap between this segment and the next
            if (i + 1 < visualTier.Count)
            {
                var gap = visualTier[i + 1].StartTime - seg.EndTime;
                if (gap > 0.05)
                {
                    var gapDur = gap.ToString("F3", invariant);
                    var gapStart = seg.EndTime.ToString("F3", invariant);
                    var gapLabel = $"gap{i}";
                    filter.Append($"[0:v]trim=start={gapStart}:duration={gapDur},setpts=PTS-STARTPTS,format=yuv420p,setsar=1[{gapLabel}];");
                    clipLabels.Add(gapLabel);
                }
            }

            clipIndex++;
        }

        // Concat all clips
        var concatInputs = string.Concat(clipLabels.Select(l => $"[{l}]"));
        var concatOut = "vconcat";
        filter.Append($"{concatInputs}concat=n={clipLabels.Count}:v=1:a=0[{concatOut}];");
        var currentVideo = concatOut;

        // ── Audio mixing (same as overlay pipeline) ──
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
            for (int ai = 0; ai < audioSegments.Count; ai++)
            {
                var aseg = audioSegments[ai];
                var aInputIdx = concatAudioInputMap[ai];
                var delayMs = (long)Math.Round(aseg.StartTime * 1000);
                var aduration = aseg.EndTime - aseg.StartTime;
                var aSrcOffset = Math.Max(0, aseg.SourceOffsetSeconds).ToString("F3", invariant);
                var clipLabel = $"aclip{ai}";

                if (aseg.IsLooping)
                {
                    filter.Append($"[{aInputIdx}:a]aloop=loop=-1:size=2147483647,");
                    filter.Append($"atrim=start=0:duration={aduration.ToString("F3", invariant)},");
                }
                else
                {
                    filter.Append($"[{aInputIdx}:a]atrim=start={aSrcOffset}:duration={aduration.ToString("F3", invariant)},");
                }
                filter.Append($"asetpts=PTS-STARTPTS,");
                filter.Append($"volume={aseg.Volume.ToString("F3", invariant)},");
                if (aseg.FadeInDuration > 0)
                    filter.Append($"afade=t=in:st=0:d={aseg.FadeInDuration.ToString("F3", invariant)},");
                if (aseg.FadeOutDuration > 0)
                    filter.Append($"afade=t=out:st={(aduration - aseg.FadeOutDuration).ToString("F3", invariant)}:d={aseg.FadeOutDuration.ToString("F3", invariant)},");
                filter.Append($"adelay={delayMs}|{delayMs},");
                filter.Append($"aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo[{clipLabel}];");
                mixLabels.Add(clipLabel);
            }

            var allMixInputs = string.Concat(mixLabels.Select(l => $"[{l}]"));
            audioOut = "amixed";
            filter.Append($"{allMixInputs}amix=inputs={mixLabels.Count}:duration=first:dropout_transition=0:normalize=0[{audioOut}]");
        }

        // Write filter script
        var filterStr = filter.ToString().TrimEnd(';');
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "pve", "fc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new UTF8Encoding(false));
        Log.Debug("Concat filter script written to: {Path} ({Length} chars)", filterScriptPath, filterStr.Length);

        args.Append($"-/filter_complex \"{filterScriptPath}\" ");
        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");

        args.Append($"-c:v {videoCodec} ");
        var concatMotionComplexity = ComputeMotionComplexity(visualTier);
        args.Append(BuildQualityArgs(videoCodec, crf, config.ResolutionWidth, config.ResolutionHeight, config.FrameRate, concatMotionComplexity));
        var preset = GetAdaptiveEncoderPreset(videoCodec, config.Quality, concatMotionComplexity);
        if (!string.IsNullOrEmpty(preset))
            args.Append($"-preset {preset} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");

        var logicalCores = Environment.ProcessorCount;
        var renderThreads = Math.Max(2, logicalCores - 2);
        var filterThreads = Math.Max(2, logicalCores / 2);
        args.Append($"-threads {renderThreads} ");
        args.Append($"-filter_threads {filterThreads} ");
        args.Append($"-filter_complex_threads {filterThreads} ");
        Log.Debug("Concat threads: {RenderThreads}/{LogicalCores} cores, filter_threads: {FilterThreads}",
            renderThreads, logicalCores, filterThreads);

        args.Append("-movflags +faststart ");
        args.Append("-max_muxing_queue_size 4096 ");

        // Duration control
        {
            var allEndTimes = visualTier.Select(s => s.EndTime)
                .Concat(audioSegments.Select(s => s.EndTime));
            var maxEnd = allEndTimes.Any() ? allEndTimes.Max() : 1.0;
            args.Append($"-t {maxEnd.ToString("F3", invariant)} ");
        }

        args.Append("-y ");
        args.Append($"\"{config.OutputPath}\"");

        Log.Debug("Concat pipeline: {Clips} clips, {Texts} texts, {Gaps} gaps, {Audio} audio",
            visualTier.Count, textTier.Count, clipLabels.Count - visualTier.Count, audioSegments.Count);

        return args.ToString();
    }

    private static string BuildLegacySingleImageCommand(RenderConfig config, int crf)
    {
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var scaleFilter = BuildScalingFilter(config);
        var legacyQualityArgs = BuildQualityArgs(videoCodec, crf, config.ResolutionWidth, config.ResolutionHeight, config.FrameRate, motionComplexity: 0.0);
        var legacyPreset = GetAdaptiveEncoderPreset(videoCodec, config.Quality, motionComplexity: 0.0);
        var legacyPresetArg = !string.IsNullOrEmpty(legacyPreset) ? $"-preset {legacyPreset} " : "";

        return $"-loop 1 " +
               $"-i \"{config.ImagePath}\" " +
               $"-i \"{config.AudioPath}\" " +
               $"-c:v {videoCodec} " +
               $"{legacyQualityArgs}" +
               $"{legacyPresetArg}" +
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

    private static RenderConfig StageRenderInputsIfNeeded(RenderConfig config)
    {
        if (!ShouldStageRenderInputs(config))
            return config;

        try
        {
            return StageRenderInputs(config);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Render input staging failed; continuing with original paths");
            return config;
        }
    }

    private static bool ShouldStageRenderInputs(RenderConfig config)
    {
        var totalCount = (config.VisualSegments?.Count ?? 0)
            + (config.TextSegments?.Count ?? 0)
            + (config.AudioSegments?.Count ?? 0);

        if (totalCount >= 16)
            return true;

        long pathChars = 0;
        pathChars += config.AudioPath?.Length ?? 0;
        pathChars += config.ImagePath?.Length ?? 0;
        pathChars += config.VisualSegments?.Sum(s => s.SourcePath?.Length ?? 0) ?? 0;
        pathChars += config.AudioSegments?.Sum(s => s.SourcePath?.Length ?? 0) ?? 0;

        return pathChars >= 12_000;
    }

    private static bool ShouldUseEmbeddedTimelineSources(
        IReadOnlyCollection<RenderVisualSegment> visualSegments,
        IReadOnlyCollection<RenderAudioSegment> audioSegments)
    {
        var visualUniqueCount = visualSegments
            .Select(s => BuildMediaInputKey(s.SourcePath, s.IsVideo))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var audioUniqueCount = audioSegments
            .Select(s => NormalizePathForKey(s.SourcePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Large timelines with many unique sources can still hit argument-size
        // limits even with staged short paths. Embed those sources via movie/amovie.
        if (visualUniqueCount + audioUniqueCount >= 24)
            return true;

        long totalSourcePathChars = visualSegments.Sum(s => s.SourcePath?.Length ?? 0)
            + audioSegments.Sum(s => s.SourcePath?.Length ?? 0);

        return totalSourcePathChars >= 8_000;
    }

    private static RenderConfig StageRenderInputs(RenderConfig config)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "pve", $"rs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string StagePath(string sourcePath, string prefix)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return sourcePath;

            var key = NormalizePathForKey(sourcePath);
            if (cache.TryGetValue(key, out var existing))
                return existing;

            var extension = Path.GetExtension(sourcePath);
            var stagedPath = Path.Combine(stagingRoot, $"{prefix}{cache.Count + 1:0000}{extension}");

            if (!TryCreateHardLink(stagedPath, sourcePath))
                File.Copy(sourcePath, stagedPath, overwrite: true);

            cache[key] = stagedPath;
            return stagedPath;
        }

        return new RenderConfig
        {
            AudioPath = StagePath(config.AudioPath, "a"),
            ImagePath = StagePath(config.ImagePath, "img"),
            OutputPath = config.OutputPath,
            ResolutionWidth = config.ResolutionWidth,
            ResolutionHeight = config.ResolutionHeight,
            AspectRatio = config.AspectRatio,
            Quality = config.Quality,
            FrameRate = config.FrameRate,
            VideoCodec = config.VideoCodec,
            AudioCodec = config.AudioCodec,
            ScaleMode = config.ScaleMode,
            PrimaryAudioVolume = config.PrimaryAudioVolume,
            VisualSegments = config.VisualSegments?.Select(seg => new RenderVisualSegment
            {
                SourcePath = StagePath(seg.SourcePath, seg.IsVideo ? "v" : "i"),
                StartTime = seg.StartTime,
                EndTime = seg.EndTime,
                IsVideo = seg.IsVideo,
                SourceOffsetSeconds = seg.SourceOffsetSeconds,
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
            }).ToList() ?? [],
            TextSegments = config.TextSegments?.Select(seg => new RenderTextSegment
            {
                Text = seg.Text,
                StartTime = seg.StartTime,
                EndTime = seg.EndTime,
                FontSize = seg.FontSize,
                FontColor = seg.FontColor,
                FontFilePath = string.IsNullOrWhiteSpace(seg.FontFilePath) ? null : StagePath(seg.FontFilePath, "font"),
                FontFamily = seg.FontFamily,
                IsBold = seg.IsBold,
                IsItalic = seg.IsItalic,
                XExpr = seg.XExpr,
                YExpr = seg.YExpr,
                DrawBox = seg.DrawBox,
                BoxColor = seg.BoxColor,
            }).ToList() ?? [],
            AudioSegments = config.AudioSegments?.Select(seg => new RenderAudioSegment
            {
                SourcePath = StagePath(seg.SourcePath, "bgm"),
                StartTime = seg.StartTime,
                EndTime = seg.EndTime,
                Volume = seg.Volume,
                FadeInDuration = seg.FadeInDuration,
                FadeOutDuration = seg.FadeOutDuration,
                SourceOffsetSeconds = seg.SourceOffsetSeconds,
                IsLooping = seg.IsLooping
            }).ToList() ?? []
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool TryCreateHardLink(string stagedPath, string sourcePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(stagedPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            return CreateHardLink(stagedPath, sourcePath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildMediaInputKey(string path, bool isVideo)
    {
        var normalized = NormalizePathForKey(path);
        return (isVideo ? "v|" : "i|") + normalized;
    }

    private static string NormalizePathForKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    // ── Codec mapping ───────────────────────────────────────────────────

    internal static string MapVideoCodec(string videoCodec)
    {
        if (string.IsNullOrWhiteSpace(videoCodec))
            return GetPreferredH264Encoder();

        return videoCodec.Trim().ToLowerInvariant() switch
        {
            "h264_auto" => GetPreferredH264Encoder(),
            "hevc_auto" => GetPreferredHevcEncoder(),
            // "h264" / "hevc" from older project configs → auto-detect to use GPU encoder
            "h264" or "x264" => GetPreferredH264Encoder(),
            "h265" or "hevc" or "x265" => GetPreferredHevcEncoder(),
            // Explicit encoder names pass through unchanged
            "h264_nvenc" => "h264_nvenc",
            "hevc_nvenc" => "hevc_nvenc",
            "h264_qsv" => "h264_qsv",
            "hevc_qsv" => "hevc_qsv",
            "h264_amf" => "h264_amf",
            "hevc_amf" => "hevc_amf",
            // Explicit software encoder names pass through unchanged
            "libx264" => "libx264",
            "libx265" => "libx265",
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
        if (!string.IsNullOrEmpty(_preferredH264Encoder) && !string.IsNullOrEmpty(_preferredHevcEncoder) && _gpuFilterProbed)
            return;

        lock (_encoderProbeLock)
        {
            if (!string.IsNullOrEmpty(_preferredH264Encoder) && !string.IsNullOrEmpty(_preferredHevcEncoder) && _gpuFilterProbed)
                return;

            _preferredH264Encoder = "libx264";
            _preferredHevcEncoder = "libx265";

            var ffmpegPath = FFmpegService.GetFFmpegPath();
            Log.Debug("GPU probe: FFmpeg path = '{Path}', exists = {Exists}",
                ffmpegPath ?? "(null)", !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath!));
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                // Do NOT set _gpuFilterProbed = true here!
                // FFmpegService may not be initialized yet (race condition at startup).
                // By leaving _gpuFilterProbed = false, the next call will re-probe
                // after FFmpegService has been initialized with a valid path.
                // Also reset the encoder names so the outer check doesn't short-circuit.
                _preferredH264Encoder = null;
                _preferredHevcEncoder = null;
                Log.Warning("GPU probe skipped: FFmpeg path not available yet. Will re-probe when path is set.");
                return;
            }

            try
            {
                // ── Step 1: probe encoders ──
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _gpuFilterProbed = true;
                    return;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(5));

                // ── Step 1b: probe + validate GPU encoders with a 1-frame smoke test ──
                // FFmpeg lists ALL compiled encoders regardless of actual GPU hardware.
                // E.g. an AMD-only machine still lists h264_nvenc in -encoders output.
                // We must validate each candidate with a real encode attempt and cascade
                // to the next one on failure, instead of falling straight to CPU.
                // NOTE: h264_mf / hevc_mf (Media Foundation) are NOT GPU encoders —
                // the inbox "H264 Encoder MFT" runs on CPU. We skip them entirely.

                // Detect GPU vendor to prioritise the matching encoder first.
                // This avoids ~1-2s wasted per failed validation of irrelevant encoders
                // (e.g. testing h264_nvenc on an AMD-only machine).
                var gpuVendor = DetectGpuVendor();
                Log.Debug("GPU vendor: {Vendor}", gpuVendor);

                string[] h264Candidates = gpuVendor switch
                {
                    GpuVendor.Amd    => ["h264_amf",  "h264_nvenc", "h264_qsv"],
                    GpuVendor.Intel  => ["h264_qsv",  "h264_nvenc", "h264_amf"],
                    _                => ["h264_nvenc", "h264_qsv",  "h264_amf"],  // NVIDIA or unknown
                };
                foreach (var candidate in h264Candidates)
                {
                    if (!output.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ValidateEncoder(ffmpegPath, candidate, out var h264Err))
                    {
                        _preferredH264Encoder = candidate;
                        Log.Debug("H264 GPU encoder '{Encoder}' validated", candidate);
                        break;
                    }
                    Log.Warning("H264 GPU encoder '{Encoder}' listed but failed validation: {Error}. Trying next candidate…",
                        candidate, h264Err);
                }

                string[] hevcCandidates = gpuVendor switch
                {
                    GpuVendor.Amd    => ["hevc_amf",  "hevc_nvenc", "hevc_qsv"],
                    GpuVendor.Intel  => ["hevc_qsv",  "hevc_nvenc", "hevc_amf"],
                    _                => ["hevc_nvenc", "hevc_qsv",  "hevc_amf"],
                };
                foreach (var candidate in hevcCandidates)
                {
                    if (!output.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ValidateEncoder(ffmpegPath, candidate, out var hevcErr))
                    {
                        _preferredHevcEncoder = candidate;
                        Log.Information("HEVC GPU encoder '{Encoder}' validated successfully.", candidate);
                        break;
                    }
                    Log.Warning("HEVC GPU encoder '{Encoder}' listed but failed validation: {Error}. Trying next candidate…",
                        candidate, hevcErr);
                }

                Log.Information("Preferred encoders: H264={H264}, HEVC={HEVC}", _preferredH264Encoder, _preferredHevcEncoder);

                // ── Step 2: probe GPU filter backends ──
                // Run `ffmpeg -filters` and check for GPU-accelerated scale/overlay filters.
                // Priority is vendor-aware: AMD prefers OpenCL, NVIDIA prefers CUDA, Intel prefers QSV.
                ProbeGpuFilterBackend(ffmpegPath, gpuVendor);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to probe FFmpeg hardware encoders/filters. Falling back to software.");
            }
            finally
            {
                _gpuFilterProbed = true;
            }
        }
    }

    /// <summary>
    /// Probe for GPU-accelerated filter support by checking <c>ffmpeg -filters</c> output.
    /// Then validate by running a tiny 1-frame encode with the GPU pipeline to confirm
    /// the driver actually works (some systems list filters but the driver is broken/old).
    /// Probe order is vendor-aware so each GPU vendor finds its best backend first:
    ///   NVIDIA: CUDA > QSV > Vulkan > OpenCL
    ///   AMD:    OpenCL > Vulkan > CUDA > QSV   (OpenCL is AMD's strongest FFmpeg backend)
    ///   Intel:  QSV > OpenCL > Vulkan > CUDA
    /// </summary>
    private static void ProbeGpuFilterBackend(string ffmpegPath, GpuVendor vendor)
    {
        try
        {
            var filterPsi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -filters",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
            };

            using var filterProc = Process.Start(filterPsi);
            if (filterProc == null) return;

            var filterOutput = filterProc.StandardOutput.ReadToEnd();
            filterProc.WaitForExit(TimeSpan.FromSeconds(5));

            // Vendor-aware probe order.
            // AMD: OpenCL is the strongest cross-vendor backend in FFmpeg for AMD.
            //      CapCut/Premiere also use OpenCL on AMD for GPU compositing.
            // NVIDIA: CUDA is native and lowest-latency.
            // Intel: QSV is native with dedicated silicon.
            string[] backendOrder = vendor switch
            {
                GpuVendor.Amd   => ["opencl", "vulkan", "cuda", "qsv"],
                GpuVendor.Intel => ["qsv", "opencl", "vulkan", "cuda"],
                _               => ["cuda", "qsv", "vulkan", "opencl"],  // NVIDIA or unknown
            };

            foreach (var backend in backendOrder)
            {
                var (scaleName, overlayName, backendEnum, label) = backend switch
                {
                    "cuda"   => ("scale_cuda",   "overlay_cuda",   GpuFilterBackend.Cuda,   "CUDA (NVIDIA)"),
                    "qsv"    => ("scale_qsv",    "overlay_qsv",    GpuFilterBackend.Qsv,    "QSV (Intel)"),
                    "vulkan" => ("scale_vulkan",  "overlay_vulkan", GpuFilterBackend.Vulkan, "Vulkan (cross-vendor)"),
                    "opencl" => ("scale_opencl", "overlay_opencl", GpuFilterBackend.OpenCL, "OpenCL (cross-vendor)"),
                    _ => ("", "", GpuFilterBackend.None, "")
                };

                if (string.IsNullOrEmpty(scaleName)) continue;

                if (filterOutput.Contains(scaleName, StringComparison.OrdinalIgnoreCase) &&
                    filterOutput.Contains(overlayName, StringComparison.OrdinalIgnoreCase))
                {
                    if (ValidateGpuBackend(ffmpegPath, backend))
                    {
                        _gpuFilterBackend = backendEnum;
                        Log.Information("GPU filter backend: {Label} — {Scale} + {Overlay}", label, scaleName, overlayName);
                        return;
                    }
                    Log.Information("GPU filter backend: {Label} listed but validation failed (driver issue?)", label);
                }
            }

            Log.Information("GPU filter backend: None — CPU filters (scale + overlay). " +
                "For AMD GPU acceleration, use an FFmpeg build with --enable-opencl (e.g. full_build).");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPU filter probe failed, using CPU filters");
        }
    }

    /// <summary>
    /// Run a 1-frame smoke test to verify the GPU backend actually initialises.
    /// Some systems enumerate CUDA/QSV filters but the driver doesn't work.
    /// </summary>
    private static bool ValidateGpuBackend(string ffmpegPath, string backend)
    {
        try
        {
            // Build a minimal pipeline: generate 1 frame → format on CPU → hwupload → GPU scale → hwdownload → format → null output.
            // Must use format=yuv420p before hwupload (CUDA/QSV require known CPU pixel format)
            // and format=yuv420p after hwdownload (overlay needs CPU yuv420p, not hw surface).
            var (hwArg, scaleFilter) = backend switch
            {
                "cuda"   => ("-init_hw_device cuda=gpudev -filter_hw_device gpudev",   "format=yuv420p,hwupload_cuda,scale_cuda=256:256,hwdownload,format=yuv420p"),
                "qsv"    => ("-init_hw_device qsv=gpudev -filter_hw_device gpudev",    "format=yuv420p,hwupload=extra_hw_frames=16,scale_qsv=w=256:h=256,hwdownload,format=yuv420p"),
                "vulkan" => ("-init_hw_device vulkan=gpudev -filter_hw_device gpudev", "format=yuv420p,hwupload,scale_vulkan=w=256:h=256,hwdownload,format=yuv420p"),
                "opencl" => ("-init_hw_device opencl=gpudev -filter_hw_device gpudev", "format=yuv420p,hwupload,scale_opencl=w=256:h=256,hwdownload,format=yuv420p"),
                _ => ("", "")
            };

            if (string.IsNullOrEmpty(hwArg)) return false;

            var validatePsi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error {hwArg} " +
                            $"-f lavfi -i \"color=c=black:s=256x256:r=1:d=0.04\" " +
                            $"-vf \"{scaleFilter}\" -frames:v 1 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
            };

            using var proc = Process.Start(validatePsi);
            if (proc == null) return false;

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(10));

            if (proc.ExitCode == 0)
            {
                Log.Debug("GPU backend '{Backend}' validation passed", backend);
                return true;
            }

            Log.Debug("GPU backend '{Backend}' validation failed (exit={Code}): {Err}",
                backend, proc.ExitCode, stderr.Length > 200 ? stderr[..200] : stderr);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GPU backend '{Backend}' validation threw", backend);
            return false;
        }
    }

    // ── Filter helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Detect the primary GPU vendor via PowerShell CIM (Win32_VideoController).
    /// Used to prioritise the correct hardware encoder and avoid wasting
    /// ~1-2 seconds per failed validation of an irrelevant vendor's encoder.
    /// Falls back to <see cref="GpuVendor.Unknown"/> on error.
    /// </summary>
    private static GpuVendor DetectGpuVendor()
    {
        try
        {
            // Use PowerShell Get-CimInstance instead of deprecated 'wmic'
            // (wmic was removed from Windows 11 24H2+).
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return GpuVendor.Unknown;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(5));

            // Check for vendor keywords in GPU name(s).
            // A system can have multiple GPUs (e.g. Intel iGPU + AMD dGPU).
            // Prefer discrete GPU vendor: AMD/NVIDIA > Intel.
            var upper = output.ToUpperInvariant();
            bool hasNvidia = upper.Contains("NVIDIA") || upper.Contains("GEFORCE") || upper.Contains("RTX") || upper.Contains("GTX");
            bool hasAmd    = upper.Contains("AMD") || upper.Contains("RADEON") || upper.Contains("RX ");
            bool hasIntel  = upper.Contains("INTEL") || upper.Contains("UHD") || upper.Contains("IRIS");

            if (hasNvidia && !hasAmd) return GpuVendor.Nvidia;
            if (hasAmd && !hasNvidia) return GpuVendor.Amd;
            if (hasNvidia && hasAmd)  return GpuVendor.Nvidia;  // dGPU wins
            if (hasIntel)             return GpuVendor.Intel;
            return GpuVendor.Unknown;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GPU vendor detection failed, using default probe order");
            return GpuVendor.Unknown;
        }
    }

    /// <summary>
    /// Run a 1-frame smoke test to verify a video encoder actually works.
    /// Catches cases where the encoder is listed but the GPU driver is too old
    /// (e.g. NVENC API version mismatch, QSV driver missing).
    /// </summary>
    private static bool ValidateEncoder(string ffmpegPath, string encoder, out string errorDetail)
    {
        errorDetail = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error " +
                            $"-f lavfi -i \"color=c=black:s=256x256:r=1:d=0.04\" " +
                            $"-c:v {encoder} -frames:v 1 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
            };

            using var proc = Process.Start(psi);
            if (proc == null) { errorDetail = "process failed to start"; return false; }

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(10));

            if (proc.ExitCode == 0)
            {
                Log.Debug("Encoder '{Encoder}' validation passed", encoder);
                return true;
            }

            errorDetail = stderr.Length > 300 ? stderr[..300] : stderr;
            Log.Debug("Encoder '{Encoder}' validation failed (exit={Code}): {Err}",
                encoder, proc.ExitCode, errorDetail);
            return false;
        }
        catch (Exception ex)
        {
            errorDetail = ex.Message;
            Log.Debug(ex, "Encoder '{Encoder}' validation threw", encoder);
            return false;
        }
    }

    private const string HighQualityScaleFlags = "lanczos+accurate_rnd+full_chroma_int";
    private const string FastScaleFlags = "bicubic+accurate_rnd+full_chroma_int";

    private static string BuildScaleFilter(int width, int height, bool preferSpeed = false) =>
        BuildScaleFilter(width, height, "Fill", preferSpeed);

    private static string BuildScaleFilter(int width, int height, string? scaleMode, bool preferSpeed = false)
    {
        var flags = preferSpeed ? FastScaleFlags : HighQualityScaleFlags;
        var mode = (scaleMode ?? "Fill").ToUpperInvariant();
        return mode switch
        {
            "FIT" => $"scale={width}:{height}:flags={flags}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "STRETCH" => $"scale={width}:{height}:flags={flags}",
            _ => $"scale={width}:{height}:flags={flags}:force_original_aspect_ratio=increase,crop={width}:{height}"
        };
    }

    internal static string BuildScalingFilter(RenderConfig config, bool preferSpeed = false)
    {
        var flags = preferSpeed ? FastScaleFlags : HighQualityScaleFlags;
        return config.ScaleMode switch
    {
        "Fit" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:flags={flags}:force_original_aspect_ratio=decrease,pad={config.ResolutionWidth}:{config.ResolutionHeight}:(ow-iw)/2:(oh-ih)/2",
        "Stretch" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:flags={flags}",
        _ => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:flags={flags}:force_original_aspect_ratio=increase,crop={config.ResolutionWidth}:{config.ResolutionHeight}"
    };
    }

    /// <summary>
    /// Returns the FFmpeg -preset value for software encoders (libx264/libx265) based on quality.
    /// Returns empty string for hardware encoders (nvenc/qsv/amf) which do not use this flag.
    /// Mapping: Low → veryfast (3-5× faster), Medium → fast, High → medium.
    /// </summary>
    public static string GetEncoderPreset(string videoCodec, string quality)
    {
        // NVENC presets: p1 (fastest) → p7 (slowest/best quality)
        if (videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return quality switch
            {
                "Low"  => "p1",
                "High" => "p5",
                _      => "p4",     // Medium
            };
        }

        // QSV presets
        if (videoCodec.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            return quality switch
            {
                "Low"  => "veryfast",
                "High" => "medium",
                _      => "fast",
            };
        }

        // AMF quality presets: speed (fastest) > balanced > quality (best)
        if (videoCodec.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            return quality switch
            {
                "Low"  => "speed",
                "High" => "quality",
                _      => "balanced",
            };
        }

        // Software encoders (libx264/libx265)
        var isSoftware = videoCodec is "libx264" or "libx265";
        if (!isSoftware)
            return string.Empty;

        return quality switch
        {
            "Low"  => "veryfast",
            "High" => "medium",
            _      => "fast",     // Medium quality default
        };
    }

    private static string GetAdaptiveEncoderPreset(string videoCodec, string quality, double motionComplexity)
    {
        var preset = GetEncoderPreset(videoCodec, quality);
        if (string.IsNullOrWhiteSpace(preset))
            return preset;

        if (!videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return preset;

        var minPreset = motionComplexity switch
        {
            >= 0.30d => "p5",
            >= 0.18d => "p4",
            >= 0.10d => "p3",
            >= 0.05d => "p2",
            _ => ""
        };

        if (string.IsNullOrEmpty(minPreset))
            return preset;

        var upgraded = MaxNvencPreset(preset, minPreset);
        if (!string.Equals(upgraded, preset, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information(
                "EncoderPreset: codec={Codec}, quality={Quality}, motionComplexity={Motion:F3}, preset={From}->{To}",
                videoCodec,
                quality,
                motionComplexity,
                preset,
                upgraded);
        }

        return upgraded;
    }

    private static string MaxNvencPreset(string currentPreset, string minPreset)
    {
        var current = ParseNvencPresetNumber(currentPreset);
        var minimum = ParseNvencPresetNumber(minPreset);

        if (current <= 0 || minimum <= 0)
            return currentPreset;

        return $"p{Math.Max(current, minimum)}";
    }

    private static int ParseNvencPresetNumber(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return 0;

        if (preset.Length == 2 && (preset[0] == 'p' || preset[0] == 'P') && char.IsDigit(preset[1]))
            return preset[1] - '0';

        return 0;
    }

    /// <summary>
    /// Returns the quality parameter string appropriate for the given video encoder.
    /// NVENC uses <c>-cq</c>, QSV uses <c>-global_quality</c>, AMF uses VBR peak
    /// with pre-analysis + VBAQ (matching CapCut/Premiere quality-per-bit),
    /// software encoders use <c>-crf</c>.
    /// </summary>
    internal static string BuildQualityArgs(
        string videoCodec,
        int crfValue,
        int width = 1080,
        int height = 1920,
        int frameRate = 30,
        double motionComplexity = 0.0)
    {
        var complexity = Math.Clamp(motionComplexity, 0.0d, 1.0d);
        var effectiveCrf = GetMotionAwareCrf(videoCodec, crfValue, complexity);
        var (targetBitrate, maxRate, bufSize) = GetBitrateBudgetFromCrf(effectiveCrf, width, height, frameRate, complexity);
        Log.Information(
            "EncoderBudget: codec={Codec}, crf={Crf}->{EffectiveCrf}, size={W}x{H}@{Fps}, motionComplexity={Motion:F3}, b:v={Target}, maxrate={MaxRate}, bufsize={BufSize}",
            videoCodec,
            crfValue,
            effectiveCrf,
            width,
            height,
            frameRate,
            complexity,
            targetBitrate,
            maxRate,
            bufSize);

        if (videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            // VBR with CQ target: better quality-per-bit than constant QP.
            // -spatial-aq 1: adaptive quantization for spatial detail (~10-15% quality gain)
            // -temporal-aq 1: temporal AQ for scene motion
            // -rc-lookahead 32: look-ahead frames for rate control decisions
            // -b_ref_mode middle: B-frame as reference (~5-10% better compression)
            // Cap average/peak bitrate to avoid oversized exports on high-motion scenes.
            return $"-rc vbr -cq {effectiveCrf} -b:v {targetBitrate} -maxrate {maxRate} -bufsize {bufSize} " +
                   $"-spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -b_ref_mode middle ";
        }
        if (videoCodec.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return $"-global_quality {effectiveCrf} -b:v {targetBitrate} -maxrate {maxRate} -bufsize {bufSize} ";
        if (videoCodec.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            // VBR peak rate control with pre-analysis and VBAQ for quality parity
            // with NVENC.
            // -rc vbr_peak: variable bitrate with peak constraint — better
            //   quality-per-bit than constant QP (matches NVENC's vbr mode).
            // -preanalysis true: AMF pre-analysis pass — scene-aware QP adjustment
            //   analogous to NVENC's -rc-lookahead (10-20% better compression).
            // -vbaq true: Variance-Based Adaptive Quantization — AMF's equivalent
            //   of NVENC's -spatial-aq (allocates bits to complex regions).
            // -enforce_hrd true: keeps bitrate within decoder buffer limits for
            //   smooth playback on mobile/web (CapCut does this by default).
            // Keep explicit bitrate caps to prevent oversized outputs.
                 return $"-rc vbr_peak -qp_i {effectiveCrf} -qp_p {effectiveCrf} -b:v {targetBitrate} -maxrate {maxRate} -bufsize {bufSize} " +
                   $"-preanalysis true -vbaq true -enforce_hrd true ";
        }
        // Software encoders: keep CRF-based quality but add VBV caps so fallback-to-CPU
        // does not produce oversized files on high-motion timelines.
        if (videoCodec is "libx264" or "libx265")
            return $"-crf {effectiveCrf} -maxrate {maxRate} -bufsize {bufSize} ";

        // Unknown encoder: preserve current behavior.
        return $"-crf {effectiveCrf} ";
    }

    private static int GetMotionAwareCrf(string videoCodec, int crfValue, double motionComplexity)
    {
        if (!videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return crfValue;

        var delta = motionComplexity switch
        {
            >= 0.30d => 4,
            >= 0.18d => 3,
            >= 0.10d => 2,
            >= 0.05d => 1,
            _ => 0
        };

        return Math.Max(22, crfValue - delta);
    }

    private static (string TargetBitrate, string MaxRate, string BufSize) GetBitrateBudgetFromCrf(
        int crfValue,
        int width,
        int height,
        int frameRate,
        double motionComplexity)
    {
        // Baseline profile for budget scaling: 1080x1920 @ 30fps.
        // Scale bitrate with pixel rate so quality/size stays more consistent
        // across resolution and framerate presets.
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var safeFps = Math.Clamp(frameRate, 1, 120);
        var pixelRate = (double)safeWidth * safeHeight * safeFps;
        var baselineRate = 1080d * 1920d * 30d;
        var scale = Math.Clamp(pixelRate / baselineRate, 0.35d, 2.2d);
        var complexity = Math.Clamp(motionComplexity, 0.0d, 1.0d);
        var motionBoost = 1.0d + complexity * 1.2d;

        if (crfValue >= 28)
            return ToBudget(1200, scale, complexity, motionBoost, motionFloorBaseKbps: 3200); // Low

        if (crfValue <= 18)
            return ToBudget(3200, scale, complexity, motionBoost, motionFloorBaseKbps: 5600); // High

        return ToBudget(2200, scale, complexity, motionBoost, motionFloorBaseKbps: 4200); // Medium
    }

    private static (string TargetBitrate, string MaxRate, string BufSize) ToBudget(
        int baseTargetKbps,
        double scale,
        double motionComplexity,
        double motionBoost,
        int motionFloorBaseKbps)
    {
        var target = (int)Math.Round(baseTargetKbps * scale * motionBoost);
        if (motionComplexity > 0.05d)
        {
            var motionFloor = (int)Math.Round(motionFloorBaseKbps * scale);
            target = Math.Max(target, motionFloor);
        }

        var maxMultiplier = motionComplexity > 0.05d ? 1.7d : 1.5d;
        var bufMultiplier = motionComplexity > 0.05d ? 3.0d : 2.0d;
        var max = (int)Math.Round(target * maxMultiplier);
        var buf = (int)Math.Round(target * bufMultiplier);

        return ($"{target}k", $"{max}k", $"{buf}k");
    }

    private static double ComputeMotionComplexity(IReadOnlyCollection<RenderVisualSegment> visualSegments)
    {
        if (visualSegments.Count == 0)
            return 0.0d;

        var stills = visualSegments
            .Where(v => !v.IsVideo && !v.HasAlpha)
            .ToList();
        if (stills.Count == 0)
            return 0.0d;

        var motionStills = stills
            .Where(v => !string.IsNullOrWhiteSpace(v.MotionPreset) && v.MotionPreset != MotionPresets.None)
            .ToList();
        if (motionStills.Count == 0)
            return 0.0d;

        var density = (double)motionStills.Count / stills.Count;
        var avgIntensity = motionStills.Average(v => Math.Clamp(v.MotionIntensity, 0.0d, 1.0d));
        return Math.Clamp(density * (0.55d + 0.45d * avgIntensity), 0.0d, 1.0d);
    }

    /// <summary>
    /// Builds a comma-prefixed FFmpeg fade filter string for a segment with transition configured.
    /// Returns an empty string when no transition is set.
    /// The fade uses alpha=1 so it affects transparency rather than color, ensuring adjacent
    /// segments cross-dissolve naturally through the composite base canvas.
    /// Format: ",fade=t=in:st=0:d={dur}:alpha=1,fade=t=out:st={outSt}:d={dur}:alpha=1"
    /// </summary>
    private static string BuildFadeFilter(RenderVisualSegment seg, IFormatProvider invariant)
    {
        if (!string.Equals(seg.TransitionType, "fade", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var dur = seg.TransitionDuration;
        if (dur <= 0 || dur > seg.Duration / 2.0)
            return string.Empty;

        // fade=out starts at (Duration - transitionDuration) seconds into the clip
        var outStart = Math.Max(0, seg.Duration - dur);
        return $",fade=t=in:st=0:d={dur.ToString("F3", invariant)}:alpha=1" +
               $",fade=t=out:st={outStart.ToString("F3", invariant)}:d={dur.ToString("F3", invariant)}:alpha=1";
    }

    /// <summary>
    /// Builds a comma-prefixed drawbox tint overlay filter for visual darkening/tint.
    /// Accepts #RRGGBB and legacy #AARRGGBB; alpha channel in color is ignored because
    /// opacity is controlled by <see cref="RenderVisualSegment.OverlayOpacity"/>.
    /// Returns an empty string when tint is disabled or color format is invalid.
    /// </summary>
    private static string BuildOverlayTintFilter(RenderVisualSegment seg, IFormatProvider invariant)
    {
        if (seg.OverlayOpacity <= 0 || string.IsNullOrWhiteSpace(seg.OverlayColorHex))
            return string.Empty;

        var hex = seg.OverlayColorHex.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 8)
            hex = hex[2..];

        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            return string.Empty;

        var alpha = Math.Clamp(seg.OverlayOpacity, 0.0, 1.0).ToString("0.###", invariant);
        return $",drawbox=x=0:y=0:w=iw:h=ih:color=0x{hex}@{alpha}:t=fill";
    }

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
#pragma warning restore CS0618
