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

    // ── GPU filter backend detection ────────────────────────────────────
    // Probed once alongside encoder detection. Determines whether the FFmpeg
    // filter graph can run scale/overlay on GPU (CUDA, QSV, Vulkan, or OpenCL).
    // When a backend is available the compose methods emit hwupload + GPU filters
    // instead of CPU libswscale, keeping decoded frames on the GPU and avoiding
    // costly CPU↔GPU round-trips. Falls back gracefully to CPU if none found.

    /// <summary>GPU filter backends in preference order.</summary>
    public enum GpuFilterBackend { None, Cuda, Qsv, Vulkan, OpenCL }

    /// <summary>GPU hardware vendor, detected via WMI to prioritise the correct encoder.</summary>
    private enum GpuVendor { Unknown, Nvidia, Amd, Intel }

    private static GpuFilterBackend _gpuFilterBackend = GpuFilterBackend.None;
    private static bool _gpuFilterProbed;

    /// <summary>Returns the detected GPU filter backend (probe runs lazily on first call).</summary>
    public static GpuFilterBackend DetectedGpuBackend
    {
        get
        {
            EnsurePreferredEncodersInitialized(); // also probes GPU filters
            return _gpuFilterBackend;
        }
    }

    /// <summary>
    /// Returns the FFmpeg <c>-init_hw_device</c> argument and the <c>-filter_hw_device</c>
    /// name for the detected GPU backend.  Empty strings when no GPU backend is available.
    /// </summary>
    internal static (string initHwDevice, string filterHwDevice, string hwuploadFilter, string scaleFilter, string overlayFilter) GetGpuFilterArgs(int width, int height)
    {
        return _gpuFilterBackend switch
        {
            GpuFilterBackend.Cuda => (
                "-init_hw_device cuda=gpudev -filter_hw_device gpudev",
                "gpudev",
                "hwupload_cuda",
                $"scale_cuda={width}:{height}:force_original_aspect_ratio=0",
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
    }

    /// <summary>
    /// Returns the hwupload filter string for the detected GPU backend.
    /// Used in filter_complex chains: <c>format=yuv420p,{hwupload},scale_cuda=...,hwdownload,format=yuv420p</c>.
    /// </summary>
    public static string GpuHwuploadFilter() => _gpuFilterBackend switch
    {
        GpuFilterBackend.Cuda   => "hwupload_cuda",
        GpuFilterBackend.Qsv    => "hwupload=extra_hw_frames=64",
        GpuFilterBackend.Vulkan => "hwupload",
        GpuFilterBackend.OpenCL => "hwupload",
        _ => ""
    };

    /// <summary>
    /// Returns the GPU-specific scale filter string for the given dimensions.
    /// Centralises the backend switch that was previously duplicated 4+ times.
    /// </summary>
    internal static string BuildGpuScaleExact(int width, int height) => _gpuFilterBackend switch
    {
        GpuFilterBackend.Cuda   => $"scale_cuda={width}:{height}",
        GpuFilterBackend.Qsv    => $"scale_qsv=w={width}:h={height}",
        GpuFilterBackend.Vulkan => $"scale_vulkan=w={width}:h={height}",
        GpuFilterBackend.OpenCL => $"scale_opencl=w={width}:h={height}",
        _ => $"scale={width}:{height}"
    };

    // ── GPU capabilities (exposed to UI layer) ──────────────────────────

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
        public bool IsGpuFiltering => FilterBackend != GpuFilterBackend.None;

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
            // AMD AMF without full build: GPU encode/decode works, but composite falls back to CPU.
            (true,  false) => $"GPU encode ({EncoderLabel}) + D3D11VA decode — CPU composite (upgrading FFmpeg for OpenCL…)",
            (false, true)  => $"GPU composite ({FilterBackend}) + CPU encode — downloading compatible FFmpeg…",
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
        var qualityArgs = BuildQualityArgs(videoCodec, crf);
        var encPreset = GetEncoderPreset(videoCodec, config.Quality);
        var presetArg = !string.IsNullOrEmpty(encPreset) ? $"-preset {encPreset} " : "";

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

        // Detect GPU backend once
        var gpuBackend = DetectedGpuBackend;
        var useGpuDecode = gpuBackend != GpuFilterBackend.None;

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

        // ── GPU hardware device init (when available) ──
        // Placed before all inputs so that -hwaccel can reference the device.
        // This lets decode → scale run on the same GPU without CPU round-trip.
        if (useGpuDecode)
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

        // ── Inputs 1..N : visual sources
        // -thread_queue_size 512: prevents buffer underrun when many inputs compete
        // for decode bandwidth. Default (8) is too small for 10+ concurrent inputs.
        // Track whether CUDA zero-copy decode is active. When true, decoded
        // video frames stay in VRAM (CUDA surfaces), eliminating one PCIe
        // upload per frame in the filter graph (no hwupload_cuda needed).
        var cudaZeroCopy = gpuBackend == GpuFilterBackend.Cuda;

        foreach (var seg in visualSegments)
        {
            if (seg.IsVideo)
            {
                if (cudaZeroCopy)
                {
                    // CUDA zero-copy: decode on GPU, keep frames in VRAM.
                    // -hwaccel_output_format cuda prevents the automatic download
                    // to system memory, giving ~2× decode throughput (per NVIDIA docs).
                    // The filter graph can use scale_cuda directly without hwupload.
                    args.Append($"-thread_queue_size 512 -hwaccel cuda -hwaccel_output_format cuda -i \"{seg.SourcePath}\" ");
                }
                else
                {
                    // Non-CUDA (QSV/Vulkan/OpenCL/CPU): use D3D11VA — the Windows
                    // DirectX 11 Video Acceleration API.  Works on ALL modern Windows 10+
                    // GPUs (NVIDIA/AMD/Intel) without a specific driver version requirement,
                    // identical to what CapCut/Premiere use for decode.  Frames are
                    // automatically copied to system memory before the filter graph, so no
                    // filter-graph changes are needed.  FFmpeg silently falls back to
                    // software decode on hardware that doesn't support D3D11VA.
                    args.Append($"-thread_queue_size 512 -hwaccel d3d11va -i \"{seg.SourcePath}\" ");
                }
            }
            else
            {
                // Limit looped image duration to prevent infinite frame generation.
                // Without -t, "-loop 1" produces an INFINITE stream that FFmpeg must
                // process for every output frame, even when the overlay is disabled.
                var loopDur = (seg.EndTime - seg.StartTime + 0.5).ToString("F3", invariant);
                args.Append($"-thread_queue_size 512 -loop 1 -t {loopDur} -i \"{seg.SourcePath}\" ");
            }
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
        var (_, _, gpuHwupload, gpuScale, gpuOverlay) = GetGpuFilterArgs(config.ResolutionWidth, config.ResolutionHeight);
        var hasGpuFilters = gpuBackend != GpuFilterBackend.None;

        // Log GPU pipeline diagnostics
        var isGpuEncoder = videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
                           videoCodec.Contains("qsv",   StringComparison.OrdinalIgnoreCase) ||
                           videoCodec.Contains("amf",   StringComparison.OrdinalIgnoreCase);
        if (cudaZeroCopy)
            Log.Information("Render: CUDA zero-copy decode — frames stay in VRAM");
        if (gpuBackend == GpuFilterBackend.Cuda)
            Log.Information("Render: CUDA full-GPU pipeline — scale_cuda + overlay_cuda, encoder {Encoder}", videoCodec);
        else if (hasGpuFilters)
            Log.Information("Render: GPU backend {Backend} for scale (overlay on CPU), encoder {Encoder}", gpuBackend, videoCodec);
        else if (isGpuEncoder)
            Log.Information("Render: GPU encode ({Encoder}) + D3D11VA decode — CPU composite", videoCodec);
        else
            Log.Information("Render: CPU-only pipeline (no GPU acceleration), encoder {Encoder}", videoCodec);

        // ── GPU compositing policy ─────────────────────────────────────
        // overlay_cuda does not support the 'enable' timeline option in current
        // FFmpeg builds, so segment start/end timing is unreliable with it.
        // CPU overlay (with enable='between(t,start,end)') is fully reliable and
        // is the standard path used by CapCut, Premiere, etc.
        // GPU acceleration is preserved where it matters most:
        //   • Input decode  : -hwaccel cuda (CUDA) or -hwaccel d3d11va (all GPUs)
        //   • Output encode : h264_nvenc / hevc_nvenc / h264_qsv / h264_amf (GPU encoder)
        // Compositing (scale + overlay) runs on CPU, matching CapCut's CPU+GPU profile.
        // Re-enable useCudaOverlay when overlay_cuda supports timeline expressions.
        var useCudaOverlay = false;

        // Step 1: base canvas
        if (useCudaOverlay)
            filter.Append($"[0:v]format=yuv420p,hwupload_cuda,setsar=1[base];");
        else
            filter.Append($"[0:v]format=yuv420p,setsar=1[base];");

        // Track whether each scaled segment is a CUDA surface (true) or CPU frame (false).
        var segOnGpu = new bool[visualSegments.Count];

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

            var isPngOverlay = seg.SourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var needsAlpha = isPngOverlay || seg.HasAlpha;

            // Build optional fade-in/fade-out filter for this segment
            var fadeFilter = BuildFadeFilter(seg, invariant);
            var hasFade = !string.IsNullOrEmpty(fadeFilter);

            // Segments that MUST stay on CPU (alpha blending, zoompan, fade)
            var forceCpu = needsAlpha || hasFade;

            if (seg.IsVideo)
            {
                if (useCudaOverlay && !forceCpu)
                {
                    // CUDA path: keep as CUDA surface for overlay_cuda.
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);

                    filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
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

                    filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                    if (cudaZeroCopy)
                        filter.Append($"{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                    else
                        filter.Append($"format=yuv420p,{GpuHwuploadFilter()},{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                }
                else
                {
                    // CPU fallback (alpha-needed or no GPU backend)
                    var pixFmt = needsAlpha ? "rgba" : "yuv420p";
                    var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                        ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                        : BuildScalingFilter(config);
                    filter.Append($"[{inputIdx}:v]trim=start={srcOffset}:duration={duration},setpts=PTS-STARTPTS,");
                    filter.Append($"format={pixFmt},{scaleFilter},setsar=1{fadeFilter}[{scaledLabel}];");
                }
            }
            else
            {
                var pixFmt = needsAlpha ? "rgba" : "yuv420p";
                var scaleFilter = (seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue)
                    ? $"scale={seg.ScaleWidth.Value}:{seg.ScaleHeight.Value}"
                    : BuildScalingFilter(config);

                // Check for Ken Burns motion effect (zoom/pan) — CPU only
                var zoompanFilter = MotionFilterBuilder.BuildZoompanFilter(seg, config.FrameRate,
                    config.ResolutionWidth, config.ResolutionHeight);

                if (zoompanFilter != null)
                {
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{zoompanFilter},setsar=1{fadeFilter}[{scaledLabel}];");
                }
                else if (useCudaOverlay && !forceCpu)
                {
                    // GPU scale for non-alpha images, keep as CUDA surface.
                    var (scaleW, scaleH) = seg.ScaleWidth.HasValue && seg.ScaleHeight.HasValue
                        ? (seg.ScaleWidth.Value, seg.ScaleHeight.Value)
                        : (config.ResolutionWidth, config.ResolutionHeight);
                    var gpuScaleExact = BuildGpuScaleExact(scaleW, scaleH);
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,");
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
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,");
                    filter.Append($"format=yuv420p,{GpuHwuploadFilter()},{gpuScaleExact},hwdownload,format=yuv420p,setsar=1{fadeFilter}[{scaledLabel}];");
                }
                else
                {
                    filter.Append($"[{inputIdx}:v]trim=duration={duration},setpts=PTS-STARTPTS,format={pixFmt},{scaleFilter},setsar=1{fadeFilter}[{scaledLabel}];");
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

        // Write filter to a temp script file (short path to save command-line chars)
        var filterScriptPath = Path.Combine(Path.GetTempPath(), "pve", "fc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filterScriptPath)!);
        File.WriteAllText(filterScriptPath, filterStr, new UTF8Encoding(false));
        Log.Debug("Filter script written to: {Path}\n{Content}", filterScriptPath, filterStr);

        // -filter_complex_script was removed in FFmpeg 7.1; use -/filter_complex to
        // read the graph from a file (FFmpeg itself suggests this in the deprecation msg).
        args.Append($"-/filter_complex \"{filterScriptPath}\" ");

        args.Append($"-map \"[{currentVideo}]\" -map \"[{audioOut}]\" ");

        args.Append($"-c:v {videoCodec} ");
        args.Append(BuildQualityArgs(videoCodec, crf));
        var preset = GetEncoderPreset(videoCodec, config.Quality);
        if (!string.IsNullOrEmpty(preset))
            args.Append($"-preset {preset} ");
        args.Append("-pix_fmt yuv420p ");
        args.Append($"-r {config.FrameRate} ");
        args.Append($"-c:a {audioCodec} ");
        // -threads 0: auto-detect optimal thread count for encoder.
        // -filter_threads 4: parallelize individual filter operations (overlay, scale)
        //   that are normally single-threaded. Significantly reduces wall time when
        //   the filter graph has many overlay chains.
        // -filter_complex_threads 4: parallelize independent filter graph branches
        //   (e.g. separate visual segment scale+motion operations run concurrently).
        args.Append("-threads 0 ");
        args.Append("-filter_threads 4 ");
        args.Append("-filter_complex_threads 4 ");
        args.Append("-movflags +faststart ");
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

    private static string BuildLegacySingleImageCommand(RenderConfig config, int crf)
    {
        var videoCodec = MapVideoCodec(config.VideoCodec);
        var audioCodec = MapAudioCodec(config.AudioCodec);
        var scaleFilter = BuildScalingFilter(config);
        var legacyQualityArgs = BuildQualityArgs(videoCodec, crf);
        var legacyPreset = GetEncoderPreset(videoCodec, config.Quality);
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
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                _gpuFilterProbed = true;
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
                Log.Information("Detected GPU vendor: {Vendor}", gpuVendor);

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
                        Log.Information("H264 GPU encoder '{Encoder}' validated successfully.", candidate);
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
    /// Detect the primary GPU vendor via WMI (Win32_VideoController).
    /// Used to prioritise the correct hardware encoder and avoid wasting
    /// ~1-2 seconds per failed validation of an irrelevant vendor's encoder.
    /// Falls back to <see cref="GpuVendor.Unknown"/> on error.
    /// </summary>
    private static GpuVendor DetectGpuVendor()
    {
        try
        {
            // Use 'wmic' rather than WMI COM interop to avoid a dependency on
            // System.Management (which is not included in most .NET project templates).
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "path win32_VideoController get Name /value",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return GpuVendor.Unknown;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(3));

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

    internal static string BuildScalingFilter(RenderConfig config) => config.ScaleMode switch
    {
        "Fit" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=decrease,pad={config.ResolutionWidth}:{config.ResolutionHeight}:(ow-iw)/2:(oh-ih)/2",
        "Stretch" => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}",
        _ => $"scale={config.ResolutionWidth}:{config.ResolutionHeight}:force_original_aspect_ratio=increase,crop={config.ResolutionWidth}:{config.ResolutionHeight}"
    };

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

    /// <summary>
    /// Returns the quality parameter string appropriate for the given video encoder.
    /// NVENC uses <c>-cq</c>, QSV uses <c>-global_quality</c>, AMF uses VBR peak
    /// with pre-analysis + VBAQ (matching CapCut/Premiere quality-per-bit),
    /// software encoders use <c>-crf</c>.
    /// </summary>
    internal static string BuildQualityArgs(string videoCodec, int crfValue)
    {
        if (videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            // VBR with CQ target: better quality-per-bit than constant QP.
            // -spatial-aq 1: adaptive quantization for spatial detail (~10-15% quality gain)
            // -temporal-aq 1: temporal AQ for scene motion
            // -rc-lookahead 32: look-ahead frames for rate control decisions
            // -b_ref_mode middle: B-frame as reference (~5-10% better compression)
            // -b:v 0: uncapped bitrate, let CQ target drive quality
            return $"-rc vbr -cq {crfValue} -b:v 0 " +
                   $"-spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -b_ref_mode middle ";
        }
        if (videoCodec.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return $"-global_quality {crfValue} ";
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
            // -b:v 0: uncapped average bitrate, let QP target drive quality.
            return $"-rc vbr_peak -qp_i {crfValue} -qp_p {crfValue} -b:v 0 " +
                   $"-preanalysis true -vbaq true -enforce_hrd true ";
        }
        // Software encoder (libx264, libx265)
        return $"-crf {crfValue} ";
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
