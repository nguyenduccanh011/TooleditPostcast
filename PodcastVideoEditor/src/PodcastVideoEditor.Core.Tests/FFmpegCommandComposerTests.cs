#nullable enable
#pragma warning disable CS0618 // Tests still exercise legacy TextSegments compatibility paths.
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

/// <summary>
/// Unit tests for FFmpegCommandComposer helpers added as part of the
/// render-performance improvements:
///   - GetEncoderPreset: software-only preset selection
///   - BuildFadeFilter: alpha fade-in/fade-out transition filter string
/// </summary>
public class FFmpegCommandComposerTests
{
    // ═══════════════════════════════════════════════════════════════════
    // GetEncoderPreset
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("libx264", "Low",    "veryfast")]
    [InlineData("libx264", "Medium", "fast")]
    [InlineData("libx264", "High",   "medium")]
    [InlineData("libx265", "Low",    "veryfast")]
    [InlineData("libx265", "Medium", "fast")]
    [InlineData("libx265", "High",   "medium")]
    public void GetEncoderPreset_SoftwareEncoders_ReturnsCorrectPreset(
        string codec, string quality, string expectedPreset)
    {
        var result = FFmpegCommandComposer.GetEncoderPreset(codec, quality);
        Assert.Equal(expectedPreset, result);
    }

    [Theory]
    [InlineData("h264_nvenc", "Low",    "p1")]
    [InlineData("h264_nvenc", "High",   "p5")]
    [InlineData("hevc_nvenc", "Medium", "p4")]
    [InlineData("h264_qsv",   "Low",    "veryfast")]
    [InlineData("hevc_qsv",   "High",   "medium")]
    public void GetEncoderPreset_GpuEncoders_ReturnsCorrectPreset(string codec, string quality, string expectedPreset)
    {
        var result = FFmpegCommandComposer.GetEncoderPreset(codec, quality);
        Assert.Equal(expectedPreset, result);
    }

    [Theory]
    [InlineData("h264_amf",   "Low",    "speed")]
    [InlineData("h264_amf",   "Medium", "balanced")]
    [InlineData("h264_amf",   "High",   "quality")]
    [InlineData("hevc_amf",   "Low",    "speed")]
    [InlineData("hevc_amf",   "Medium", "balanced")]
    [InlineData("hevc_amf",   "High",   "quality")]
    public void GetEncoderPreset_AmfEncoders_ReturnsCorrectPreset(string codec, string quality, string expectedPreset)
    {
        var result = FFmpegCommandComposer.GetEncoderPreset(codec, quality);
        Assert.Equal(expectedPreset, result);
    }

    [Theory]
    [InlineData("libx264", "Unknown", "fast")]  // unknown quality falls back to Medium/fast
    [InlineData("libx264", "",        "fast")]
    public void GetEncoderPreset_UnknownQuality_FallsBackToFast(string codec, string quality, string expected)
    {
        var result = FFmpegCommandComposer.GetEncoderPreset(codec, quality);
        Assert.Equal(expected, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BuildFadeFilter (tested via MapVideoCodec path indirectly via
    // the internal helper exposed through the Build() contract)
    // — We white-box test via the composed filter string.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildFadeFilter_NoTransition_ReturnsEmpty()
    {
        var seg = new RenderVisualSegment
        {
            StartTime       = 0,
            EndTime         = 5,
            TransitionType  = "none",
            TransitionDuration = 0.5
        };
        // We verify through the public Build() contract: inject a minimal config
        // that exercises the fade path. Since BuildFadeFilter is private, we
        // check indirectly that a "none" transition produces no fade= in the filter.
        var config = BuildMinimalConfig(seg, "none");
        var (args, _) = FFmpegCommandComposer.Build(config);
        Assert.DoesNotContain("fade=", args);
    }

    [Fact]
    public void BuildFadeFilter_FadeTransition_InsertsFadeFilter()
    {
        var seg = new RenderVisualSegment
        {
            SourcePath         = "C:/test.png",
            StartTime          = 0,
            EndTime            = 5,
            TransitionType     = "fade",
            TransitionDuration = 0.5
        };
        // Build a temp PNG so File.Exists passes
        var tempPng = CreateTempPng();
        seg.SourcePath = tempPng;
        try
        {
            var config = BuildMinimalConfig(seg, "fade");
            var (args, _) = FFmpegCommandComposer.Build(config);
            // -/filter_complex is the FFmpeg 7.1+ file-read syntax (replacement for
            // removed -filter_complex_script). Check args reference the script file.
            Assert.Contains("-/filter_complex", args);
            // Read the generated filter script
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");
            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));
            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("fade=t=in:st=0", script);
            Assert.Contains("fade=t=out", script);
            Assert.Contains("alpha=1", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void BuildFadeFilter_TransitionDurationExceedsHalfSegment_NoFade()
    {
        // If TransitionDuration > Duration/2, fade must be skipped to avoid overlap
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath         = tempPng,
                StartTime          = 0,
                EndTime            = 1,       // 1s segment
                TransitionType     = "fade",
                TransitionDuration = 0.6      // > 0.5 (half of 1s) → should skip
            };
            var config = BuildMinimalConfig(seg, "fade");
            var (args, _) = FFmpegCommandComposer.Build(config);
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            if (scriptMatch.Success)
            {
                var script = System.IO.File.ReadAllText(scriptMatch.Groups[1].Value);
                Assert.DoesNotContain("fade=t=in", script);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Preset appears in final FFmpeg args
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SoftwareCodecLowQuality_ContainsVeryfastPreset()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng, StartTime = 0, EndTime = 5
            };
            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth  = 1080,
                ResolutionHeight = 1920,
                FrameRate        = 30,
                VideoCodec       = "h264",    // maps to libx264
                AudioCodec       = "aac",
                Quality          = "Low",
                VisualSegments   = [seg],
                TextSegments     = [],
                AudioSegments    = []
            };
            var (args, _) = FFmpegCommandComposer.Build(config);
            Assert.Contains("-preset veryfast", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_SoftwareCodecHighQuality_ContainsMediumPreset()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng, StartTime = 0, EndTime = 5
            };
            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth  = 1080,
                ResolutionHeight = 1920,
                FrameRate        = 30,
                VideoCodec       = "h264",
                AudioCodec       = "aac",
                Quality          = "High",
                VisualSegments   = [seg],
                TextSegments     = [],
                AudioSegments    = []
            };
            var (args, _) = FFmpegCommandComposer.Build(config);
            Assert.Contains("-preset medium", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // hwaccel d3d11va for video inputs (non-CUDA path)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_VideoSegment_ContainsHwaccelAuto()
    {
        var tempMp4 = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"test_{System.Guid.NewGuid():N}.mp4");
        System.IO.File.WriteAllBytes(tempMp4, []); // create empty file so File.Exists passes
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempMp4,
                StartTime  = 0,
                EndTime    = 5,
                IsVideo    = true
            };
            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth  = 1080,
                ResolutionHeight = 1920,
                FrameRate        = 30,
                VideoCodec       = "h264",
                AudioCodec       = "aac",
                Quality          = "Medium",
                VisualSegments   = [seg],
                TextSegments     = [],
                AudioSegments    = []
            };
            var (args, _) = FFmpegCommandComposer.Build(config);
            // Uses -hwaccel auto to let FFmpeg pick best decoder (d3d11va → dxva2 → sw)
            // across all Windows GPUs without hard-coding a specific backend.
            Assert.Contains("-hwaccel auto", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempMp4)) System.IO.File.Delete(tempMp4);
        }
    }

    [Fact]
    public void Build_RepeatedVisualSource_UsesSingleInputReference()
    {
        var tempPng = CreateTempPng();
        try
        {
            // Overlap segments to force overlay pipeline (not concat) and verify input dedup there.
            var seg1 = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 8,
                ZOrder = 0
            };

            var seg2 = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 2,
                EndTime = 10,
                ZOrder = 1
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg1, seg2],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);
            var quotedInput = $"-i \"{tempPng}\"";
            Assert.Equal(1, CountOccurrences(args, quotedInput));
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_ImageOnlySegment_DoesNotContainHwaccel()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime  = 0,
                EndTime    = 5,
                IsVideo    = false
            };
            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth  = 1080,
                ResolutionHeight = 1920,
                FrameRate        = 30,
                VideoCodec       = "h264",
                AudioCodec       = "aac",
                Quality          = "Medium",
                VisualSegments   = [seg],
                TextSegments     = [],
                AudioSegments    = []
            };
            var (args, _) = FFmpegCommandComposer.Build(config);
            Assert.DoesNotContain("-hwaccel auto", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_ImageMotionSegment_UsesSingleFrameFeedBeforeZoompan()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5,
                IsVideo = false,
                MotionPreset = MotionPresets.PanLeft,
                MotionIntensity = 0.6
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("zoompan=", script);
            Assert.Contains("select='eq(n,0)'", script);
            Assert.Contains("format=rgba", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_ImageSegment_WithExplicitBounds_UsesFillCoverCrop()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5,
                ScaleWidth = 600,
                ScaleHeight = 400,
                ScaleMode = "Fill"
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(args, "-/filter_complex \"([^\"]+)\"");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("force_original_aspect_ratio=increase", script);
            Assert.Contains("crop=600:400", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_ImageSegment_WithExplicitBounds_Stretch_DoesNotForceAspectCrop()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5,
                ScaleWidth = 600,
                ScaleHeight = 400,
                ScaleMode = "Stretch"
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(args, "-/filter_complex \"([^\"]+)\"");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("scale=600:400:flags=", script);
            Assert.DoesNotContain("force_original_aspect_ratio=increase", script);
            Assert.DoesNotContain("crop=600:400", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_NvencCodec_UsesBitrateCaps_NotUncappedVbr()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264_nvenc",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.Contains("-rc vbr", args);
            Assert.Contains("-cq 23", args);
            Assert.Contains("-b:v 2200k", args);
            Assert.Contains("-maxrate 3300k", args);
            Assert.Contains("-bufsize 4400k", args);
            Assert.DoesNotContain("-b:v 0", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_NvencCodec_WithImageMotion_UsesHigherBitrateBudget()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 12.5,
                MotionPreset = MotionPresets.ZoomIn,
                MotionIntensity = 1.0,
                IsVideo = false
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264_nvenc",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.Contains("-rc vbr", args);
            var cqMatch = System.Text.RegularExpressions.Regex.Match(args, @"-cq\s+(\d+)");
            var bMatch = System.Text.RegularExpressions.Regex.Match(args, @"-b:v\s+(\d+)k");
            var maxMatch = System.Text.RegularExpressions.Regex.Match(args, @"-maxrate\s+(\d+)k");
            var bufMatch = System.Text.RegularExpressions.Regex.Match(args, @"-bufsize\s+(\d+)k");
            var presetMatch = System.Text.RegularExpressions.Regex.Match(args, @"-preset\s+(p\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            Assert.True(cqMatch.Success, "-cq not found in ffmpeg args");
            Assert.True(bMatch.Success, "-b:v bitrate not found in ffmpeg args");
            Assert.True(maxMatch.Success, "-maxrate not found in ffmpeg args");
            Assert.True(bufMatch.Success, "-bufsize not found in ffmpeg args");
            Assert.True(presetMatch.Success, "-preset not found in ffmpeg args");

            var cq = int.Parse(cqMatch.Groups[1].Value);
            var b = int.Parse(bMatch.Groups[1].Value);
            var max = int.Parse(maxMatch.Groups[1].Value);
            var buf = int.Parse(bufMatch.Groups[1].Value);
            var preset = presetMatch.Groups[1].Value.ToLowerInvariant();

            Assert.True(cq <= 26, $"Expected motion-aware CQ <= 26, got {cq}");
            Assert.True(b > 2200, $"Expected motion-aware target bitrate > 2200k, got {b}k");
            Assert.True(max > 3300, $"Expected motion-aware maxrate > 3300k, got {max}k");
            Assert.True(buf > 4400, $"Expected motion-aware bufsize > 4400k, got {buf}k");
            Assert.True(preset is "p2" or "p3" or "p4" or "p5" or "p6" or "p7", $"Expected motion-aware NVENC preset >= p2, got {preset}");
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_AmfCodec_UsesBitrateCaps_NotUncappedVbr()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264_amf",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.Contains("-rc vbr_peak", args);
            Assert.Contains("-qp_i 23", args);
            Assert.Contains("-qp_p 23", args);
            Assert.Contains("-b:v 2200k", args);
            Assert.Contains("-maxrate 3300k", args);
            Assert.Contains("-bufsize 4400k", args);
            Assert.DoesNotContain("-b:v 0", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_SoftwareCodec_UsesVbvCaps_ToAvoidOversizedOutputs()
    {
        var tempPng = CreateTempPng();
        try
        {
            var seg = new RenderVisualSegment
            {
                SourcePath = tempPng,
                StartTime = 0,
                EndTime = 5
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "libx264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [seg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.Contains("-crf 23", args);
            Assert.Contains("-maxrate 3300k", args);
            Assert.Contains("-bufsize 4400k", args);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    [Fact]
    public void Build_LargeTimeline_EmbedsVisualSourcesWithMovieFilter()
    {
        var tempImages = new List<string>();
        try
        {
            var visuals = new List<RenderVisualSegment>();
            for (int i = 0; i < 25; i++)
            {
                var img = CreateTempPng();
                tempImages.Add(img);
                visuals.Add(new RenderVisualSegment
                {
                    SourcePath = img,
                    StartTime = 0,
                    EndTime = 10,
                    IsVideo = false,
                    ZOrder = i
                });
            }

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = visuals,
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.DoesNotContain("-thread_queue_size 512 -loop 1 -i", args);
            Assert.Contains("-/filter_complex", args);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("movie=filename='", script);
        }
        finally
        {
            foreach (var img in tempImages)
            {
                if (System.IO.File.Exists(img))
                    System.IO.File.Delete(img);
            }
        }
    }

    [Fact]
    public void Build_LargeTimeline_EmbedsAudioSourcesWithAmovieFilter()
    {
        var tempPng = CreateTempPng();
        var tempAudio = new List<string>();
        try
        {
            var audioSegments = new List<RenderAudioSegment>();
            for (int i = 0; i < 24; i++)
            {
                var audioPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"test_audio_{System.Guid.NewGuid():N}.wav");
                System.IO.File.WriteAllBytes(audioPath, []);
                tempAudio.Add(audioPath);

                audioSegments.Add(new RenderAudioSegment
                {
                    SourcePath = audioPath,
                    StartTime = i,
                    EndTime = i + 2,
                    Volume = 1.0
                });
            }

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments =
                [
                    new RenderVisualSegment
                    {
                        SourcePath = tempPng,
                        StartTime = 0,
                        EndTime = 30,
                        IsVideo = false
                    }
                ],
                TextSegments = [],
                AudioSegments = audioSegments
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            Assert.DoesNotContain("-thread_queue_size 512 -i", args);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("amovie=filename='", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);

            foreach (var audio in tempAudio)
            {
                if (System.IO.File.Exists(audio))
                    System.IO.File.Delete(audio);
            }
        }
    }

    [Fact]
    public void Build_ConcatPipeline_WithMixedClipSizes_NormalizesEachClipToCanvasBeforeConcat()
    {
        var tempPngA = CreateTempPng();
        var tempPngB = CreateTempPng();
        try
        {
            var segA = new RenderVisualSegment
            {
                SourcePath = tempPngA,
                StartTime = 0,
                EndTime = 3,
                ScaleWidth = 1080,
                ScaleHeight = 1080,
                ZOrder = 0
            };

            var segB = new RenderVisualSegment
            {
                SourcePath = tempPngB,
                StartTime = 3,
                EndTime = 6,
                ZOrder = 0
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [segA, segB],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("[clipbg0][vbase0]overlay=x=0:y=0", script);
            Assert.Contains("[clipbg1][vbase1]overlay=x=0:y=0", script);
            Assert.Contains("concat=n=2:v=1:a=0[vconcat]", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPngA))
                System.IO.File.Delete(tempPngA);
            if (System.IO.File.Exists(tempPngB))
                System.IO.File.Delete(tempPngB);
        }
    }

    [Fact]
    public void Build_ConcatPipeline_WithUnmatchedTextTier_FallsBackToOverlayPipeline()
    {
        var tempPngA = CreateTempPng();
        var tempPngB = CreateTempPng();
        var tempTextPng = CreateTempPng();
        try
        {
            var segA = new RenderVisualSegment
            {
                SourcePath = tempPngA,
                StartTime = 0,
                EndTime = 2,
                ZOrder = 0
            };

            var segB = new RenderVisualSegment
            {
                SourcePath = tempPngB,
                StartTime = 4,
                EndTime = 6,
                ZOrder = 0
            };

            var textTierSeg = new RenderVisualSegment
            {
                SourcePath = tempTextPng,
                StartTime = 1,
                EndTime = 5,
                ZOrder = 10000,
                ScaleWidth = 800,
                ScaleHeight = 120
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [segA, segB, textTierSeg],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.DoesNotContain("concat=n=", script);
            Assert.Contains("overlay=x=0:y=0", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPngA))
                System.IO.File.Delete(tempPngA);
            if (System.IO.File.Exists(tempPngB))
                System.IO.File.Delete(tempPngB);
            if (System.IO.File.Exists(tempTextPng))
                System.IO.File.Delete(tempTextPng);
        }
    }

    [Fact]
    public void Build_OverlayPipeline_WithTrackTint_EmitsDrawboxFilter()
    {
        var tempPngA = CreateTempPng();
        var tempPngB = CreateTempPng();
        try
        {
            // Overlap forces overlay pipeline (not concat).
            var segA = new RenderVisualSegment
            {
                SourcePath = tempPngA,
                StartTime = 0,
                EndTime = 4,
                ZOrder = 0,
                OverlayColorHex = "#000000",
                OverlayOpacity = 0.7
            };

            var segB = new RenderVisualSegment
            {
                SourcePath = tempPngB,
                StartTime = 2,
                EndTime = 6,
                ZOrder = 1
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [segA, segB],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("drawbox=x=0:y=0:w=iw:h=ih:color=0x000000@0.7:t=fill", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPngA))
                System.IO.File.Delete(tempPngA);
            if (System.IO.File.Exists(tempPngB))
                System.IO.File.Delete(tempPngB);
        }
    }

    [Fact]
    public void Build_ConcatPipeline_WithTrackTint_EmitsDrawboxFilter()
    {
        var tempPngA = CreateTempPng();
        var tempPngB = CreateTempPng();
        try
        {
            // Sequential segments use concat pipeline.
            var segA = new RenderVisualSegment
            {
                SourcePath = tempPngA,
                StartTime = 0,
                EndTime = 3,
                ZOrder = 0,
                OverlayColorHex = "#000000",
                OverlayOpacity = 0.7
            };

            var segB = new RenderVisualSegment
            {
                SourcePath = tempPngB,
                StartTime = 3,
                EndTime = 6,
                ZOrder = 0
            };

            var config = new RenderConfig
            {
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Quality = "Medium",
                VisualSegments = [segA, segB],
                TextSegments = [],
                AudioSegments = []
            };

            var (args, _) = FFmpegCommandComposer.Build(config);

            var scriptMatch = System.Text.RegularExpressions.Regex.Match(
                args, @"-/filter_complex ""([^""]+)""");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("concat=n=2:v=1:a=0[vconcat]", script);
            Assert.Contains("drawbox=x=0:y=0:w=iw:h=ih:color=0x000000@0.7:t=fill", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPngA))
                System.IO.File.Delete(tempPngA);
            if (System.IO.File.Exists(tempPngB))
                System.IO.File.Delete(tempPngB);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string CreateTempPng()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"test_{System.Guid.NewGuid():N}.png");
        System.IO.File.WriteAllBytes(path, []); // empty file, just needs to exist for File.Exists
        return path;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static RenderConfig BuildMinimalConfig(RenderVisualSegment seg, string _transitionHint)
    {
        return new RenderConfig
        {
            OutputPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "test_output.mp4"),
            ResolutionWidth  = 1080,
            ResolutionHeight = 1920,
            FrameRate        = 30,
            VideoCodec       = "h264",
            AudioCodec       = "aac",
            Quality          = "Medium",
            VisualSegments   = [seg],
            TextSegments     = [],
            AudioSegments    = []
        };
    }
}
#pragma warning restore CS0618
