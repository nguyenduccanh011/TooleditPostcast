using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class FFmpegServiceChunkingTests
{
    [Fact]
    public void BuildChunkConfigForWindow_PreservesPrimaryAudioVolume_AndDisablesEmbeddedSources()
    {
        var source = new RenderConfig
        {
            AudioPath = "C:/audio.wav",
            ImagePath = "C:/image.png",
            OutputPath = "C:/out.mp4",
            ResolutionWidth = 1080,
            ResolutionHeight = 1920,
            AspectRatio = "9:16",
            Quality = "High",
            FrameRate = 30,
            VideoCodec = "h264_auto",
            AudioCodec = "aac",
            ScaleMode = "Fill",
            PrimaryAudioVolume = 0.42
        };

        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/v.png",
                StartTime = 0,
                EndTime = 10
            }
        };

        var audios = new List<RenderAudioSegment>
        {
            new()
            {
                SourcePath = "C:/bgm.wav",
                StartTime = 1,
                EndTime = 5,
                Volume = 0.5
            }
        };

        var chunk = FFmpegService.BuildChunkConfigForWindow(source, "C:/chunk.mp4", visuals, audios);

        Assert.Equal(0.42, chunk.PrimaryAudioVolume);
        Assert.Equal("C:/audio.wav", chunk.AudioPath);
        Assert.True(chunk.UseGpuOverlay);
        Assert.True(chunk.DisableEmbeddedTimelineSources);
        Assert.Same(visuals, chunk.VisualSegments);
        Assert.Same(audios, chunk.AudioSegments);
    }
}
