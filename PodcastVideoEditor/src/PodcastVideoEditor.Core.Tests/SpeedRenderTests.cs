#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

/// <summary>
/// Verifies CapCut-style clip speed is applied to the composed FFmpeg filter graph:
/// a sped video clip consumes (slotDuration × Speed) of source, compressed back into
/// the slot via setpts division. Speed 1.0 must leave the graph unchanged (no regression).
///
/// Two visual segments are used so the composer builds the multi-segment overlay chain
/// (filter_complex), where the per-segment trim/setpts lives. Source files must physically
/// exist or the composer drops them during staging.
/// </summary>
public class SpeedRenderTests : IDisposable
{
    private readonly string _dir;

    public SpeedRenderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pve_speedtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeFile(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[64]);
        return p;
    }

    private static string ComposeScript(RenderConfig config)
    {
        var (args, _) = FFmpegCommandComposer.Build(config);
        var m = Regex.Match(args, "-/filter_complex \"([^\"]+)\"");
        Assert.True(m.Success, "filter_complex script path not found. ARGS=" + args);
        return File.ReadAllText(m.Groups[1].Value);
    }

    private RenderConfig TwoVideoConfig(double firstSpeed) => new()
    {
        OutputPath = Path.Combine(_dir, "out.mp4"),
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
                SourcePath = MakeFile("a.mp4"),
                StartTime = 0, EndTime = 4,   // 4s slot
                IsVideo = true, Speed = firstSpeed,
                TransitionType = "none", TransitionDuration = 0
            },
            new RenderVisualSegment
            {
                SourcePath = MakeFile("b.mp4"),
                StartTime = 4, EndTime = 8,
                IsVideo = true, Speed = 1.0,
                TransitionType = "none", TransitionDuration = 0
            }
        ],
        TextSegments = [],
        AudioSegments = []
    };

    [Fact]
    public void Speed2x_Video_TrimsDoubleSourceAndCompressesPts()
    {
        var script = ComposeScript(TwoVideoConfig(2.0));

        Assert.Contains("duration=8.000", script);                    // 4s slot × 2.0 → 8s source
        Assert.Contains("setpts=(PTS-STARTPTS)/2.000000", script);    // played at 2×
    }

    [Fact]
    public void Speed0_5x_Video_TrimsHalfSourceAndStretchesPts()
    {
        var script = ComposeScript(TwoVideoConfig(0.5));

        Assert.Contains("duration=2.000", script);                    // 4s × 0.5 → 2s source
        Assert.Contains("setpts=(PTS-STARTPTS)/0.500000", script);
    }

    [Fact]
    public void Speed1x_Video_LeavesFilterUnchanged()
    {
        var script = ComposeScript(TwoVideoConfig(1.0));

        Assert.DoesNotContain("(PTS-STARTPTS)/", script);             // no division at default speed
        Assert.Contains("setpts=PTS-STARTPTS", script);
    }
}
