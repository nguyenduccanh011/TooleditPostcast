using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class RenderSegmentBuilderOverlayTests
{
    [Fact]
    public void BuildTimelineVisualSegments_UsesTrackOverlayByDefault()
    {
        var imagePath = Path.GetTempFileName();

        try
        {
            var project = new Project
            {
                Assets =
                [
                    new Asset
                    {
                        Id = "asset-1",
                        FilePath = imagePath,
                        Type = "image"
                    }
                ],
                Tracks =
                [
                    new Track
                    {
                        Id = "track-1",
                        TrackType = TrackTypes.Visual,
                        IsVisible = true,
                        OverlayColorHex = "#112233",
                        OverlayOpacity = 0.45,
                        Segments =
                        [
                            new Segment
                            {
                                Id = "seg-1",
                                Kind = SegmentKinds.Visual,
                                BackgroundAssetId = "asset-1",
                                StartTime = 0,
                                EndTime = 3,
                                OverlayColorHex = null,
                                OverlayOpacity = null
                            }
                        ]
                    }
                ]
            };

            var segments = RenderSegmentBuilder.BuildTimelineVisualSegments(
                project,
                renderWidth: 1920,
                renderHeight: 1080,
                elements: null,
                canvasWidth: 1920,
                canvasHeight: 1080);

            var visual = Assert.Single(segments);
            Assert.Equal("#112233", visual.OverlayColorHex);
            Assert.Equal(0.45, visual.OverlayOpacity, 3);
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
    }

    [Fact]
    public void BuildTimelineVisualSegments_UsesSegmentOverlayOverride()
    {
        var imagePath = Path.GetTempFileName();

        try
        {
            var project = new Project
            {
                Assets =
                [
                    new Asset
                    {
                        Id = "asset-1",
                        FilePath = imagePath,
                        Type = "image"
                    }
                ],
                Tracks =
                [
                    new Track
                    {
                        Id = "track-1",
                        TrackType = TrackTypes.Visual,
                        IsVisible = true,
                        OverlayColorHex = "#112233",
                        OverlayOpacity = 0.45,
                        Segments =
                        [
                            new Segment
                            {
                                Id = "seg-1",
                                Kind = SegmentKinds.Visual,
                                BackgroundAssetId = "asset-1",
                                StartTime = 0,
                                EndTime = 3,
                                OverlayColorHex = "#abcdef",
                                OverlayOpacity = 0.8
                            }
                        ]
                    }
                ]
            };

            var segments = RenderSegmentBuilder.BuildTimelineVisualSegments(
                project,
                renderWidth: 1920,
                renderHeight: 1080,
                elements: null,
                canvasWidth: 1920,
                canvasHeight: 1080);

            var visual = Assert.Single(segments);
            Assert.Equal("#abcdef", visual.OverlayColorHex);
            Assert.Equal(0.8, visual.OverlayOpacity, 3);
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
    }
}
