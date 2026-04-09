using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class RenderSegmentBuilderAudioTests
{
    [Fact]
    public void BuildTimelineAudioSegments_SkipsSegmentMatchingPrimaryAudio()
    {
        var primaryAudioPath = Path.GetTempFileName();

        try
        {
            var project = new Project
            {
                AudioPath = primaryAudioPath,
                Assets = new List<Asset>
                {
                    new()
                    {
                        Id = "asset-audio",
                        FilePath = primaryAudioPath,
                        Type = "audio"
                    }
                },
                Tracks = new List<Track>
                {
                    new()
                    {
                        Id = "audio-track",
                        TrackType = TrackTypes.Audio,
                        IsVisible = true,
                        Segments = new List<Segment>
                        {
                            new()
                            {
                                Id = "seg-1",
                                Kind = SegmentKinds.Audio,
                                BackgroundAssetId = "asset-audio",
                                StartTime = 20,
                                EndTime = 50,
                                SourceStartOffset = 0
                            }
                        }
                    }
                }
            };

            var segments = RenderSegmentBuilder.BuildTimelineAudioSegments(project);

            Assert.Empty(segments);
        }
        finally
        {
            if (File.Exists(primaryAudioPath))
                File.Delete(primaryAudioPath);
        }
    }

    [Fact]
    public void BuildTimelineAudioSegments_KeepsSegmentWhenDifferentFromPrimaryAudio()
    {
        var primaryAudioPath = Path.GetTempFileName();
        var extraAudioPath = Path.GetTempFileName();

        try
        {
            var project = new Project
            {
                AudioPath = primaryAudioPath,
                Assets = new List<Asset>
                {
                    new()
                    {
                        Id = "asset-extra",
                        FilePath = extraAudioPath,
                        Type = "audio"
                    }
                },
                Tracks = new List<Track>
                {
                    new()
                    {
                        Id = "audio-track",
                        TrackType = TrackTypes.Audio,
                        IsVisible = true,
                        Segments = new List<Segment>
                        {
                            new()
                            {
                                Id = "seg-1",
                                Kind = SegmentKinds.Audio,
                                BackgroundAssetId = "asset-extra",
                                StartTime = 20,
                                EndTime = 50,
                                SourceStartOffset = 0
                            }
                        }
                    }
                }
            };

            var segments = RenderSegmentBuilder.BuildTimelineAudioSegments(project);

            var segment = Assert.Single(segments);
            Assert.Equal(extraAudioPath, segment.SourcePath);
            Assert.Equal(20, segment.StartTime);
            Assert.Equal(50, segment.EndTime);
        }
        finally
        {
            if (File.Exists(primaryAudioPath))
                File.Delete(primaryAudioPath);
            if (File.Exists(extraAudioPath))
                File.Delete(extraAudioPath);
        }
    }
}
