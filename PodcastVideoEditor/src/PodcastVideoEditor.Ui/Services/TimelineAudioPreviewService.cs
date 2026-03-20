#nullable enable
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PodcastVideoEditor.Ui.Services;

internal sealed class TimelineAudioPreviewService
{
    private readonly IAudioTimelinePreviewService _audioService;
    private readonly Func<double, List<(Track track, Segment segment)>> _getActiveSegmentsAtTime;
    private readonly Func<IEnumerable<Track>> _getTracks;
    private readonly Func<Project?> _getCurrentProject;
    private readonly Func<double> _getTotalDuration;

    public TimelineAudioPreviewService(
        IAudioTimelinePreviewService audioService,
        Func<double, List<(Track track, Segment segment)>> getActiveSegmentsAtTime,
        Func<IEnumerable<Track>> getTracks,
        Func<Project?> getCurrentProject,
        Func<double> getTotalDuration)
    {
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _getActiveSegmentsAtTime = getActiveSegmentsAtTime ?? throw new ArgumentNullException(nameof(getActiveSegmentsAtTime));
        _getTracks = getTracks ?? throw new ArgumentNullException(nameof(getTracks));
        _getCurrentProject = getCurrentProject ?? throw new ArgumentNullException(nameof(getCurrentProject));
        _getTotalDuration = getTotalDuration ?? throw new ArgumentNullException(nameof(getTotalDuration));
    }

    public void SyncPreviewAudio(double playheadSeconds, bool forceResync = false)
    {
        try
        {
            var activeSegments = _getActiveSegmentsAtTime(playheadSeconds);

            Segment? audioSegment = null;
            foreach (var (track, segment) in activeSegments)
            {
                if (string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(segment.BackgroundAssetId))
                {
                    audioSegment = segment;
                    break;
                }
            }

            if (audioSegment == null)
            {
                if (_audioService.CurrentSegmentId != null)
                {
                    Log.Debug("No active audio segment at {Time}s, stopping segment audio", playheadSeconds);
                    _audioService.StopSegmentAudio();
                }

                PreloadNextAudioSegment(playheadSeconds);
            }
            else
            {
                var asset = _getCurrentProject()?.Assets?
                    .FirstOrDefault(candidate => candidate.Id == audioSegment.BackgroundAssetId);

                if (asset == null || string.IsNullOrEmpty(asset.FilePath))
                {
                    Log.Debug(
                        "Audio segment {SegmentId} has no asset (AssetId={AssetId}, AssetsCount={Count})",
                        audioSegment.Id,
                        audioSegment.BackgroundAssetId,
                        _getCurrentProject()?.Assets?.Count ?? -1);
                    _audioService.StopSegmentAudio();
                }
                else
                {
                    _audioService.PlaySegmentAudio(
                        audioSegment.Id,
                        asset.FilePath,
                        audioSegment.StartTime,
                        playheadSeconds,
                        CalculateSegmentVolume(audioSegment, playheadSeconds),
                        audioSegment.SourceStartOffset,
                        forceResync);
                }
            }

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error in segment audio playback update");
        }
    }

    private void PreloadNextAudioSegment(double playheadSeconds)
    {
        try
        {
            Segment? nextSegment = null;
            foreach (var track in _getTracks())
            {
                if (!track.IsVisible || !string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (track.Segments == null)
                    continue;

                foreach (var segment in track.Segments)
                {
                    if (segment.StartTime > playheadSeconds && !string.IsNullOrEmpty(segment.BackgroundAssetId))
                    {
                        if (nextSegment == null || segment.StartTime < nextSegment.StartTime)
                            nextSegment = segment;
                    }
                }
            }

            if (nextSegment == null)
                return;

            var asset = _getCurrentProject()?.Assets?
                .FirstOrDefault(candidate => candidate.Id == nextSegment.BackgroundAssetId);

            if (asset != null && !string.IsNullOrEmpty(asset.FilePath))
                _audioService.PreloadSegmentAudio(nextSegment.Id, asset.FilePath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error preloading next audio segment");
        }
    }

    private static float CalculateSegmentVolume(Segment segment, double playheadSeconds)
    {
        float baseVolume = (float)segment.Volume;
        double elapsed = playheadSeconds - segment.StartTime;
        double remaining = segment.EndTime - playheadSeconds;

        if (segment.FadeInDuration > 0 && elapsed < segment.FadeInDuration)
            baseVolume *= (float)(elapsed / segment.FadeInDuration);

        if (segment.FadeOutDuration > 0 && remaining < segment.FadeOutDuration)
            baseVolume *= (float)(remaining / segment.FadeOutDuration);

        return Math.Clamp(baseVolume, 0f, 1f);
    }
}