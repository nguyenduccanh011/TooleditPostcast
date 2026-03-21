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

    // Tracks which segment IDs were active on the previous sync call so we can
    // stop individual segments as soon as they leave the playhead window.
    private HashSet<string> _lastActiveSegmentIds = new();

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

            // Collect ALL active audio segments across every audio track
            var audioSegments = new List<(Segment segment, string filePath)>();
            foreach (var (track, segment) in activeSegments)
            {
                if (!string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(segment.BackgroundAssetId))
                    continue;

                var asset = _getCurrentProject()?.Assets?
                    .FirstOrDefault(candidate => candidate.Id == segment.BackgroundAssetId);

                if (asset == null || string.IsNullOrEmpty(asset.FilePath))
                {
                    Log.Debug(
                        "Audio segment {SegmentId} has no asset (AssetId={AssetId})",
                        segment.Id, segment.BackgroundAssetId);
                    continue;
                }

                audioSegments.Add((segment, asset.FilePath));
            }

            if (audioSegments.Count == 0)
            {
                // Nothing should be playing — stop everything currently in the mixer
                if (_audioService.CurrentSegmentId != null)
                {
                    Log.Debug("No active audio segment at {Time}s, stopping all segment audio", playheadSeconds);
                    _audioService.StopSegmentAudio();
                }
                _lastActiveSegmentIds.Clear();
                PreloadNextAudioSegment(playheadSeconds);
                return;
            }

            // Build the current active ID set
            var currentIds = new HashSet<string>(audioSegments.Count);
            foreach (var (segment, _) in audioSegments)
                currentIds.Add(segment.Id);

            // Stop any segments that were playing but are no longer in the active window
            foreach (var oldId in _lastActiveSegmentIds)
            {
                if (!currentIds.Contains(oldId))
                {
                    Log.Debug("Segment {Id} left active window at {Time}s, stopping", oldId, playheadSeconds);
                    _audioService.StopSegmentAudio(oldId);
                }
            }
            _lastActiveSegmentIds = currentIds;

            // Start / update every currently active audio segment
            foreach (var (segment, filePath) in audioSegments)
            {
                _audioService.PlaySegmentAudio(
                    segment.Id,
                    filePath,
                    segment.StartTime,
                    playheadSeconds,
                    CalculateSegmentVolume(segment, playheadSeconds),
                    segment.SourceStartOffset,
                    forceResync);
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