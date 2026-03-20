using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

public sealed class TimelineAudioPreviewServiceTests
{
    [Fact]
    public void SyncPreviewAudio_StopsCurrentSegment_AndPreloadsNextAudioSegment()
    {
        var audio = new FakeAudioTimelinePreviewService { CurrentSegmentIdValue = "current" };
        var nextSegment = new Segment
        {
            Id = "next-segment",
            StartTime = 12,
            EndTime = 18,
            BackgroundAssetId = "next-asset",
            Kind = SegmentKinds.Visual,
            TrackId = "audio-track"
        };
        var preview = CreatePreviewService(
            audio,
            _ => [],
            [CreateAudioTrack(nextSegment)],
            CreateProjectWithAssets(("next-asset", "c:\\media\\next.wav")));

        preview.SyncPreviewAudio(5);

        Assert.Equal(1, audio.StopSegmentAudioCallCount);
        Assert.Equal(("next-segment", "c:\\media\\next.wav"), Assert.Single(audio.PreloadCalls));
    }

    [Fact]
    public void SyncPreviewAudio_PlaysActiveSegment_WithFadeAdjustedVolume()
    {
        var audio = new FakeAudioTimelinePreviewService();
        var activeSegment = new Segment
        {
            Id = "active",
            StartTime = 10,
            EndTime = 20,
            BackgroundAssetId = "asset-1",
            Volume = 0.8,
            FadeInDuration = 4,
            FadeOutDuration = 0,
            Kind = SegmentKinds.Visual,
            TrackId = "audio-track"
        };
        var project = CreateProjectWithAssets(("asset-1", "c:\\media\\clip.wav"));
        var preview = CreatePreviewService(
            audio,
            _ => [(
                new Track { Id = "audio-track", TrackType = TrackTypes.Audio, IsVisible = true },
                activeSegment)],
            [],
            project);

        preview.SyncPreviewAudio(12, forceResync: true);

        var call = Assert.Single(audio.PlaySegmentCalls);
        Assert.Equal("active", call.segmentId);
        Assert.Equal("c:\\media\\clip.wav", call.audioFilePath);
        Assert.Equal(10, call.segmentStartTime, 3);
        Assert.Equal(12, call.playheadPosition, 3);
        Assert.Equal(0.4f, call.volume, 3);
        Assert.True(call.forceResync);
    }

    private static TimelineAudioPreviewService CreatePreviewService(
        FakeAudioTimelinePreviewService audio,
        Func<double, List<(Track track, Segment segment)>> getActiveSegments,
        IEnumerable<Track> tracks,
        Project project,
        double totalDuration = 20)
    {
        return new TimelineAudioPreviewService(
            audio,
            getActiveSegments,
            () => tracks,
            () => project,
            () => totalDuration);
    }

    private static Project CreateProjectWithAssets(params (string id, string filePath)[] assets)
    {
        return new Project
        {
            Assets = assets.Select(asset => new Asset
            {
                Id = asset.id,
                FilePath = asset.filePath,
                Type = "audio"
            }).ToList()
        };
    }

    private static Track CreateAudioTrack(params Segment[] segments)
    {
        var track = new Track
        {
            Id = "audio-track",
            TrackType = TrackTypes.Audio,
            IsVisible = true
        };

        foreach (var segment in segments)
            track.Segments.Add(segment);

        return track;
    }

    private sealed class FakeAudioTimelinePreviewService : IAudioTimelinePreviewService
    {
        public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        public event EventHandler<EventArgs>? PlaybackStarted;
        public event EventHandler<EventArgs>? PlaybackPaused;

        public string? CurrentSegmentIdValue { get; set; }
        public int StopSegmentAudioCallCount { get; private set; }
        public List<(string segmentId, string audioFilePath)> PreloadCalls { get; } = new();
        public List<(string segmentId, string audioFilePath, double segmentStartTime, double playheadPosition, float volume, double sourceStartOffset, bool forceResync)> PlaySegmentCalls { get; } = new();

        public string? CurrentAudioPath => null;
        public string? CurrentSegmentId => CurrentSegmentIdValue;
        public PlaybackState PlaybackState => PlaybackState.Stopped;
        public bool IsPlaying => false;

        public Task<AudioMetadata> LoadAudioAsync(string filePath) => Task.FromResult(new AudioMetadata());
        public void Play() => PlaybackStarted?.Invoke(this, EventArgs.Empty);
        public void Pause() => PlaybackPaused?.Invoke(this, EventArgs.Empty);
        public void Stop() => PlaybackStopped?.Invoke(this, new PlaybackStoppedEventArgs());
        public void Seek(double positionSeconds) { }
        public double GetCurrentPosition() => 0;
        public double GetDuration() => 0;
        public float[] GetPeakSamples(int binCount) => [];
        public void SetVolume(float volume) { }
        public float GetVolume() => 1;

        public void PreloadSegmentAudio(string segmentId, string audioFilePath)
        {
            PreloadCalls.Add((segmentId, audioFilePath));
        }

        public void PlaySegmentAudio(string segmentId, string audioFilePath, double segmentStartTime, double playheadPosition, float volume, double sourceStartOffset = 0, bool forceResync = false)
        {
            PlaySegmentCalls.Add((segmentId, audioFilePath, segmentStartTime, playheadPosition, volume, sourceStartOffset, forceResync));
            CurrentSegmentIdValue = segmentId;
        }

        public void StopSegmentAudio()
        {
            StopSegmentAudioCallCount++;
            CurrentSegmentIdValue = null;
        }

        public void Dispose()
        {
        }
    }
}