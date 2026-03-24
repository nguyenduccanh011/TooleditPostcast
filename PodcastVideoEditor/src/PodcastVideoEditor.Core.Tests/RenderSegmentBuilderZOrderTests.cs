using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class RenderSegmentBuilderZOrderTests
{
    /// <summary>
    /// Two visual tracks: Order 0 (front) should get HIGHER ZOrder than Order 1 (back).
    /// This verifies the critical render bug fix — previously Order 0 got the lowest ZOrder.
    /// </summary>
    [Fact]
    public void ComputeTrackZOrderMap_FrontTrackGetsHigherZOrder()
    {
        var project = new Project
        {
            Tracks = new List<Track>
            {
                new() { Id = "front", Order = 0, IsVisible = true },
                new() { Id = "back", Order = 1, IsVisible = true },
            }
        };

        var map = RenderSegmentBuilder.ComputeTrackZOrderMap(project);

        Assert.True(map["front"] > map["back"],
            $"Front track (Order=0) ZOrder {map["front"]} should be > back track (Order=1) ZOrder {map["back"]}");
    }

    /// <summary>
    /// Three tracks of mixed types: verify ordering is purely by Track.Order,
    /// not by track type. Text track between two visual tracks gets correct slot.
    /// </summary>
    [Fact]
    public void ComputeTrackZOrderMap_MixedTypes_OrderedByTrackOrderOnly()
    {
        var project = new Project
        {
            Tracks = new List<Track>
            {
                new() { Id = "v-front", Order = 0, TrackType = "Visual", IsVisible = true },
                new() { Id = "text-mid", Order = 1, TrackType = "Text", IsVisible = true },
                new() { Id = "v-back", Order = 2, TrackType = "Visual", IsVisible = true },
            }
        };

        var map = RenderSegmentBuilder.ComputeTrackZOrderMap(project);

        Assert.True(map["v-front"] > map["text-mid"]);
        Assert.True(map["text-mid"] > map["v-back"]);
    }

    /// <summary>
    /// Hidden tracks should not appear in the ZOrder map.
    /// </summary>
    [Fact]
    public void ComputeTrackZOrderMap_HiddenTracksExcluded()
    {
        var project = new Project
        {
            Tracks = new List<Track>
            {
                new() { Id = "visible", Order = 0, IsVisible = true },
                new() { Id = "hidden", Order = 1, IsVisible = false },
            }
        };

        var map = RenderSegmentBuilder.ComputeTrackZOrderMap(project);

        Assert.True(map.ContainsKey("visible"));
        Assert.False(map.ContainsKey("hidden"));
    }

    /// <summary>
    /// Each track should get a slot of ZOrderSlotSize (100) to allow room for
    /// multiple segments within that track.
    /// </summary>
    [Fact]
    public void ComputeTrackZOrderMap_SlotsAreSpacedCorrectly()
    {
        var project = new Project
        {
            Tracks = new List<Track>
            {
                new() { Id = "a", Order = 0, IsVisible = true },
                new() { Id = "b", Order = 1, IsVisible = true },
                new() { Id = "c", Order = 2, IsVisible = true },
            }
        };

        var map = RenderSegmentBuilder.ComputeTrackZOrderMap(project);

        // Back (Order=2) gets slot 0, mid (Order=1) gets slot 100, front (Order=0) gets slot 200
        Assert.Equal(0, map["c"]);
        Assert.Equal(RenderSegmentBuilder.ZOrderSlotSize, map["b"]);
        Assert.Equal(2 * RenderSegmentBuilder.ZOrderSlotSize, map["a"]);
    }

    /// <summary>
    /// Empty project → empty map, no exception.
    /// </summary>
    [Fact]
    public void ComputeTrackZOrderMap_EmptyProject_ReturnsEmptyMap()
    {
        var project = new Project { Tracks = null! };

        var map = RenderSegmentBuilder.ComputeTrackZOrderMap(project);

        Assert.Empty(map);
    }
}
