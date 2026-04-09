using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;
using System.Reflection;

namespace PodcastVideoEditor.Core.Tests;

public class FFmpegFilterGraphBuilderTests
{
    [Fact]
    public void BuildCommand_ImageMotionSegment_UsesSingleFrameFeedBeforeZoompan()
    {
        var tempPng = CreateTempPng();
        try
        {
            var config = new RenderConfig
            {
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                FrameRate = 30,
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_output.mp4"),
                VisualSegments =
                [
                    new RenderVisualSegment
                    {
                        SourcePath = tempPng,
                        StartTime = 0,
                        EndTime = 5,
                        IsVideo = false,
                        MotionPreset = MotionPresets.PanLeft,
                        MotionIntensity = 0.6
                    }
                ]
            };

            var method = typeof(FFmpegFilterGraphBuilder).GetMethod(
                "BuildCommandFromConfig",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var args = (string)method!.Invoke(null, [config, config.ScaleMode])!;
            var scriptMatch = System.Text.RegularExpressions.Regex.Match(args, "-/filter_complex \"([^\"]+)\"");
            Assert.True(scriptMatch.Success, "-/filter_complex path not found in args");

            var scriptPath = scriptMatch.Groups[1].Value;
            Assert.True(System.IO.File.Exists(scriptPath));

            var script = System.IO.File.ReadAllText(scriptPath);
            Assert.Contains("zoompan=", script);
            Assert.Contains("select='eq(n,0)'", script);
        }
        finally
        {
            if (System.IO.File.Exists(tempPng))
                System.IO.File.Delete(tempPng);
        }
    }

    private static string CreateTempPng()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"test_{System.Guid.NewGuid():N}.png");
        System.IO.File.WriteAllBytes(path, []);
        return path;
    }
}