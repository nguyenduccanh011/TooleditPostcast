using PodcastVideoEditor.Core;
using PodcastVideoEditor.Core.Models;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class TrackImageLayoutPresetTests
{
    [Theory]
    [InlineData(ImageLayoutPresets.FullFrame, 1080, 1920, 0, 0, 1080, 1920)]
    [InlineData(ImageLayoutPresets.Square_Center, 1080, 1920, 0, 420, 1080, 1080)]
    public void ComputeImageRect_ReturnsExpectedRect(
        string preset,
        double frameWidth,
        double frameHeight,
        double expectedX,
        double expectedY,
        double expectedWidth,
        double expectedHeight)
    {
        var rect = RenderHelper.ComputeImageRect(preset, frameWidth, frameHeight);

        Assert.Equal(expectedX, rect.X, 3);
        Assert.Equal(expectedY, rect.Y, 3);
        Assert.Equal(expectedWidth, rect.Width, 3);
        Assert.Equal(expectedHeight, rect.Height, 3);
    }

    [Fact]
    public void ComputeImageRect_ForWidescreenCenter_ReturnsCenteredRect()
    {
        var rect = RenderHelper.ComputeImageRect(ImageLayoutPresets.Widescreen_Center, 1080, 1920);

        Assert.Equal(0, rect.X, 3);
        Assert.Equal(656.25, rect.Y, 3);
        Assert.Equal(1080, rect.Width, 3);
        Assert.Equal(607.5, rect.Height, 3);
    }

    [Fact]
    public void ComputeImageRect_ForUnknownPreset_FallsBackToFullFrame()
    {
        var rect = RenderHelper.ComputeImageRect("UnknownPreset", 720, 1280);

        Assert.Equal(0, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
        Assert.Equal(720, rect.Width, 3);
        Assert.Equal(1280, rect.Height, 3);
    }
}