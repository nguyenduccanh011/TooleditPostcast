#nullable enable
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Threading;

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
                Interlocked.Exchange(ref _lastSyncedPlayheadBits, BitConverter.DoubleToInt64Bits(positionSeconds));
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

        /// <summary>
        /// Force-clear the scrub flag. Called before playback starts to prevent a stuck
        /// _isScrubbing from blocking the sync loop (e.g. mouse released outside canvas).
        /// </summary>
        public void ResetScrubState()
        {
            if (_isScrubbing)
            {
                _isScrubbing = false;
                _playbackCoordinator.NotifyUserInteraction();
                Log.Debug("ResetScrubState: forced _isScrubbing=false");
            }
        }

        // StartPlayheadSync / StopPlayheadSync replaced by TimelinePlaybackCoordinator.
        // The coordinator manages the sync loop, wall-clock fallback, loop, and EOF handling.

        // UpdateSegmentAudioPlayback / PreloadNextAudioSegment / CalculateSegmentVolume
        // are now handled by TimelineAudioPreviewService.
        // The coordinator calls _audioPreviewService.SyncPreviewAudio() each frame.

        /// <summary>Bindable min zoom width for slider.</summary>
        public double MinZoomWidth => TimelineConstants.MinTimelineWidth;
        /// <summary>Bindable max zoom width for slider.</summary>
        public double MaxZoomWidth => TimelineConstants.MaxTimelineWidth;

        /// <summary>
        /// Zoom timeline by factor (e.g. 1.15 = zoom in, 1/1.15 = zoom out). Call from Ctrl+MouseWheel.
        /// </summary>
        public void ZoomBy(double factor)
        {
            double newWidth = TimelineWidth * factor;
            newWidth = Math.Clamp(newWidth, TimelineConstants.MinTimelineWidth, TimelineConstants.MaxTimelineWidth);
            // Use relative threshold (0.1%) to avoid dead zones at both small and large widths
            if (Math.Abs(newWidth - TimelineWidth) / Math.Max(1, TimelineWidth) < 0.001)
                return;
            TimelineWidth = newWidth;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ZoomIn() => ZoomBy(1.25);

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ZoomOut() => ZoomBy(1.0 / 1.25);
    }
}
