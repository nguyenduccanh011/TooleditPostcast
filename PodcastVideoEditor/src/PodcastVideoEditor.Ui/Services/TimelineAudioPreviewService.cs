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
    private string? _assetCacheProjectId;
    private Dictionary<string, Asset> _assetById = new(StringComparer.Ordinal);

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
            var currentProject = _getCurrentProject();
            RefreshAssetCache(currentProject);

            var activeSegments = _getActiveSegmentsAtTime(playheadSeconds);
            var loadedPrimaryAudioPath = _audioService.CurrentAudioPath;
            var activeAudioCandidates = activeSegments.Count(pair =>
                string.Equals(pair.track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));
            Log.Debug(
                "AudioPreviewSync: t={Time:F3}s active={ActiveCount} audioCandidates={AudioCount} primary={PrimaryPath} currentSegment={CurrentSegmentId} force={ForceResync}",
                playheadSeconds,
                activeSegments.Count,
                activeAudioCandidates,
                loadedPrimaryAudioPath,
                _audioService.CurrentSegmentId,
                forceResync);

            // Collect ALL active audio segments across every audio track
            var audioSegments = new List<(Segment segment, string filePath)>();
            foreach (var (track, segment) in activeSegments)
            {
                if (!string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(segment.BackgroundAssetId))
                {
                    Log.Warning(
                        "AudioPreviewSkip: segment={SegmentId} track={TrackId} at {Time:F3}s has no BackgroundAssetId",
                        segment.Id,
                        track.Id,
                        playheadSeconds);
                    continue;
                }

                var asset = TryGetAsset(segment.BackgroundAssetId);

                if (asset == null || string.IsNullOrEmpty(asset.FilePath))
                {
                    Log.Warning(
                        "AudioPreviewSkip: segment={SegmentId} asset={AssetId} at {Time:F3}s has no asset or FilePath",
                        segment.Id,
                        segment.BackgroundAssetId,
                        playheadSeconds);
                    continue;
                }

                // Avoid double-play only when the exact same primary file is currently loaded
                // into the audio transport. In timeline-first mode CurrentAudioPath is null,
                // so timeline audio segments must still be mixed and played.
                if (!string.IsNullOrWhiteSpace(loadedPrimaryAudioPath)
                    && string.Equals(Path.GetFullPath(asset.FilePath), Path.GetFullPath(loadedPrimaryAudioPath), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug(
                        "AudioPreviewSkip: segment={SegmentId} asset={AssetId} path={Path} skipped because same primary audio is loaded",
                        segment.Id,
                        asset.Id,
                        asset.FilePath);
                    continue;
                }

                Log.Debug(
                    "AudioPreviewCandidate: segment={SegmentId} track={TrackId} asset={AssetId} path={Path} start={Start:F3}s end={End:F3}s sourceOffset={SourceOffset:F3}s volume={Volume:F3}",
                    segment.Id,
                    track.Id,
                    asset.Id,
                    asset.FilePath,
                    segment.StartTime,
                    segment.EndTime,
                    segment.SourceStartOffset,
                    segment.Volume);
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
                Log.Debug(
                    "AudioPreviewPlay: segment={SegmentId} path={Path} playhead={Playhead:F3}s segmentStart={Start:F3}s force={ForceResync}",
                    segment.Id,
                    filePath,
                    playheadSeconds,
                    segment.StartTime,
                    forceResync);
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

    private void RefreshAssetCache(Project? project)
    {
        if (project == null)
        {
            _assetCacheProjectId = null;
            _assetById = new Dictionary<string, Asset>(StringComparer.Ordinal);
            return;
        }

        // Rebuild when project changes or cache count is stale.
        int currentAssetCount = project.Assets?.Count ?? 0;
        if (string.Equals(_assetCacheProjectId, project.Id, StringComparison.Ordinal)
            && _assetById.Count == currentAssetCount)
        {
            return;
        }

        var rebuilt = new Dictionary<string, Asset>(currentAssetCount, StringComparer.Ordinal);
        if (project.Assets != null)
        {
            foreach (var asset in project.Assets)
            {
                if (!string.IsNullOrWhiteSpace(asset.Id))
                    rebuilt[asset.Id] = asset;
            }
        }

        _assetById = rebuilt;
        _assetCacheProjectId = project.Id;
    }

    private Asset? TryGetAsset(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return null;

        return _assetById.TryGetValue(assetId, out var asset) ? asset : null;
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