using PodcastVideoEditor.Core.Utilities;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class RenderSizingTests
{
    [Theory]
    [InlineData("1080p", "9:16", 1080, 1920)]
    [InlineData("1080p", "16:9", 1920, 1080)]
    [InlineData("1080p", "1:1", 1080, 1080)]
    [InlineData("720p", "4:5", 720, 900)]
    public void ResolveRenderSize_ReturnsExpectedSize(string preset, string aspectRatio, int expectedWidth, int expectedHeight)
    {
        var (width, height) = RenderSizing.ResolveRenderSize(preset, aspectRatio);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ResolveRenderSize_ForInvalidAspectRatio_FallsBackTo9By16()
    {
        var (width, height) = RenderSizing.ResolveRenderSize("1080p", "invalid");

        Assert.Equal(1080, width);
        Assert.Equal(1920, height);
    }

    [Fact]
    public void EnsureEvenDimensions_AdjustsOddValues()
    {
        var (width, height) = RenderSizing.EnsureEvenDimensions(607, 1081);

        Assert.Equal(608, width);
        Assert.Equal(1082, height);
    }

    [Fact]
    public void InferResolutionLabel_UsesClosestShortEdge()
    {
        var label = RenderSizing.InferResolutionLabel(608, 1080);

        Assert.Equal("720p", label);
    }
}
