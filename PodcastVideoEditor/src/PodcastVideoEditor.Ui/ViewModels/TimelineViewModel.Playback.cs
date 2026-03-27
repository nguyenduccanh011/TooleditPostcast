#nullable enable
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;

namespace PodcastVideoEditor.Ui.ViewModels
{
    // ── Playback: seek, scrub, zoom ──

    public partial class TimelineViewModel
    {
        /// <summary>
        /// Seek playhead (and audio) to the specified position in seconds.
        /// Must be called when user clicks on timeline - otherwise sync loop overwrites PlayheadPosition.
        /// </summary>
        public void SeekTo(double positionSeconds)
        {
            try
            {
                // Allow playhead to seek up to 30s beyond content; auto-expand timeline if needed
                double maxSeek = TotalDuration + TimelineConstants.PlayheadOvershoot;
                positionSeconds = Math.Clamp(positionSeconds, 0, maxSeek);
                if (positionSeconds > TotalDuration)
                    ExpandTimelineToFit(positionSeconds);
                _audioService.Seek(positionSeconds);
                PlayheadPosition = positionSeconds;
                _lastSyncedPlayhead = positionSeconds;
                _playbackCoordinator.NotifyUserInteraction();
                // Immediately resync all segment audio to the new position (forceResync=true)
                _audioPreviewService.SyncPreviewAudio(positionSeconds, forceResync: true);
                StatusMessage = $"Playhead: {positionSeconds:F1}s";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Seek failed: {ex.Message}";
                Log.Warning(ex, "Seek failed");
            }
        }

        /// <summary>
        /// Update playhead immediately for scrub preview without forcing costly audio seek.
        /// </summary>
        public void PreviewPlayhead(double positionSeconds)
        {
            _isScrubbing = true;
            double maxScrub = TotalDuration + TimelineConstants.PlayheadOvershoot;
            positionSeconds = Math.Clamp(positionSeconds, 0, maxScrub);
            if (positionSeconds > TotalDuration)
                ExpandTimelineToFit(positionSeconds);
            PlayheadPosition = positionSeconds;
            StatusMessage = $"Preview: {positionSeconds:F1}s";
        }

        /// <summary>
        /// Commit a seek after scrub gesture ends.
        /// </summary>
        public void CommitScrubSeek(double positionSeconds)
        {
            SeekTo(positionSeconds);
            _isScrubbing = false;
            _playbackCoordinator.NotifyUserInteraction();
            ScrubCompleted?.Invoke(this, EventArgs.Empty);
        }

        // StartPlayheadSync / StopPlayheadSync replaced by TimelinePlaybackCoordinator.
        // The coordinator manages the sync loop, wall-clock fallback, loop, and EOF handling.

        // UpdateSegmentAudioPlayback / PreloadNextAudioSegment / CalculateSegmentVolume
        // are now handled by TimelineAudioPreviewService.
        // The coordinator calls _audioPreviewService.SyncPreviewAudio() each frame.

        /// <summary>
        /// Min/max timeline width for zoom (Ctrl+wheel and slider).
        /// </summary>
        public const double MinTimelineWidth = 400;
        public const double MaxTimelineWidth = 20000;

        /// <summary>Bindable min zoom width for slider.</summary>
        public double MinZoomWidth => MinTimelineWidth;
        /// <summary>Bindable max zoom width for slider.</summary>
        public double MaxZoomWidth => MaxTimelineWidth;

        /// <summary>
        /// Zoom timeline by factor (e.g. 1.15 = zoom in, 1/1.15 = zoom out). Call from Ctrl+MouseWheel.
        /// </summary>
        public void ZoomBy(double factor)
        {
            double newWidth = TimelineWidth * factor;
            newWidth = Math.Clamp(newWidth, MinTimelineWidth, MaxTimelineWidth);
            if (Math.Abs(newWidth - TimelineWidth) < 1)
                return;
            TimelineWidth = newWidth;
        }
    }
}
