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

    [Fact]
    public void BuildChunkConfigForWindow_DisablesGpuOverlay_WhenChunkHasImageMotion()
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
            PrimaryAudioVolume = 1.0
        };

        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/v.png",
                StartTime = 0,
                EndTime = 10,
                IsVideo = false,
                MotionPreset = MotionPresets.ZoomIn,
                MotionIntensity = 1.0
            }
        };

        var chunk = FFmpegService.BuildChunkConfigForWindow(source, "C:/chunk.mp4", visuals, []);

        Assert.False(chunk.UseGpuOverlay);
        Assert.True(chunk.DisableEmbeddedTimelineSources);
    }

    [Fact]
    public void TrySplitChunkWindow_ReturnsTrue_ForLargeWindow()
    {
        var ok = FFmpegService.TrySplitChunkWindow(10.0, 22.0, out var left, out var right);

        Assert.True(ok);
        Assert.Equal(10.0, left.Start, 3);
        Assert.Equal(16.0, left.End, 3);
        Assert.Equal(16.0, right.Start, 3);
        Assert.Equal(22.0, right.End, 3);
    }

    [Fact]
    public void TrySplitChunkWindow_ReturnsFalse_ForSmallWindow()
    {
        var ok = FFmpegService.TrySplitChunkWindow(4.0, 8.5, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void BuildChunkWindows_AvoidsSplittingBoundarySensitiveMotionSegment()
    {
        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/motion.png",
                StartTime = 0,
                EndTime = 8,
                IsVideo = false,
                MotionPreset = MotionPresets.ZoomIn,
                MotionIntensity = 1.0,
                ZOrder = 0
            }
        };

        var windows = FFmpegService.BuildChunkWindows(
            visuals,
            timelineEndTime: 12,
            maxChunkDurationSeconds: 6,
            targetMaxVisualsPerChunk: 24,
            minChunkDurationSeconds: 4);

        Assert.Equal(2, windows.Count);
        Assert.Equal(0, windows[0].StartTime, 3);
        Assert.Equal(8, windows[0].EndTime, 3);
        Assert.Equal(8, windows[1].StartTime, 3);
        Assert.Equal(12, windows[1].EndTime, 3);
    }

    [Fact]
    public void BuildChunkVisualSegments_DisablesFade_WhenVisualIsClippedByChunkBoundary()
    {
        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/image.png",
                StartTime = 0,
                EndTime = 8,
                IsVideo = false,
                TransitionType = "fade",
                TransitionDuration = 1.0,
                ZOrder = 0
            }
        };

        var chunkVisuals = FFmpegService.BuildChunkVisualSegments(visuals, 0, 6, 30);

        Assert.Single(chunkVisuals);
        Assert.Equal("none", chunkVisuals[0].TransitionType);
        Assert.Equal(0, chunkVisuals[0].TransitionDuration);
        Assert.Equal(0, chunkVisuals[0].StartTime, 3);
        Assert.Equal(6, chunkVisuals[0].EndTime, 3);
    }

    [Fact]
    public void BuildChunkVisualSegments_PreservesMotionReference_WhenVisualIsClippedByChunkBoundary()
    {
        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/image.png",
                StartTime = 0,
                EndTime = 8,
                IsVideo = false,
                MotionPreset = MotionPresets.ZoomIn,
                MotionIntensity = 1.0,
                ZOrder = 0
            }
        };

        var chunkVisuals = FFmpegService.BuildChunkVisualSegments(visuals, 3, 6, 30);

        Assert.Single(chunkVisuals);
        Assert.Equal(0, chunkVisuals[0].StartTime, 3);
        Assert.Equal(3, chunkVisuals[0].EndTime, 3);
        Assert.Equal(3, chunkVisuals[0].MotionReferenceOffsetSeconds, 3);
        Assert.Equal(8, chunkVisuals[0].MotionReferenceDurationSeconds, 3);
    }

    [Fact]
    public void NormalizeChunkWindowsToFrameGrid_SnapsBoundaries_AndPreservesContiguity()
    {
        var windows = new List<(double StartTime, double EndTime)>
        {
            (0, 14.78),
            (14.78, 26.78),
            (26.78, 82.94)
        };

        var normalized = FFmpegService.NormalizeChunkWindowsToFrameGrid(windows, 82.94, 30);

        Assert.Equal(3, normalized.Count);
        Assert.Equal(0, normalized[0].StartTime, 6);
        Assert.Equal(14.766667, normalized[0].EndTime, 5);
        Assert.Equal(normalized[0].EndTime, normalized[1].StartTime, 6);
        Assert.Equal(26.766667, normalized[1].EndTime, 5);
        Assert.Equal(normalized[1].EndTime, normalized[2].StartTime, 6);
        Assert.Equal(82.94, normalized[2].EndTime, 6);
    }

    [Fact]
    public void BuildChunkVisualSegments_SkipsSubFrameBoundarySegments()
    {
        var visuals = new List<RenderVisualSegment>
        {
            new()
            {
                SourcePath = "C:/tiny.png",
                StartTime = 1.00,
                EndTime = 1.01,
                IsVideo = false,
                ZOrder = 0
            }
        };

        var chunkVisuals = FFmpegService.BuildChunkVisualSegments(visuals, 1.00, 2.00, 30);

        Assert.Empty(chunkVisuals);
    }
}
