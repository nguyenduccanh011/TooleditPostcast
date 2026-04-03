#nullable enable
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
