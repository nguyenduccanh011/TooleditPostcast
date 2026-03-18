using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class ImageAssetIngestServiceTests
{
    [Theory]
    [InlineData(4000, 3000, 2048, 2048, 1536)]
    [InlineData(3000, 4000, 2048, 1536, 2048)]
    [InlineData(1920, 1080, 2048, 1920, 1080)]
    [InlineData(1080, 1920, 2048, 1080, 1920)]
    public void ComputeNormalizedSize_KeepsAspectRatioWithinBudget(
        int width,
        int height,
        int maxLongEdge,
        int expectedWidth,
        int expectedHeight)
    {
        var result = ImageAssetIngestService.ComputeNormalizedSize(width, height, maxLongEdge);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void ComputeNormalizedSize_InvalidDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageAssetIngestService.ComputeNormalizedSize(0, 100, 2048));
    }
}