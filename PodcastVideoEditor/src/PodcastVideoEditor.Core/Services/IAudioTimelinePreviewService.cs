using System;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Timeline preview contract for segment and BGM sync.
    /// Keeps timeline preview orchestration independent from AudioService implementation details.
    /// </summary>
    public interface IAudioTimelinePreviewService : IAudioPlaybackService
    {
        string? CurrentAudioPath { get; }
        string? CurrentSegmentId { get; }
        bool IsBgmPlaying { get; }

        float[] GetPeakSamples(int binCount);
        void PreloadSegmentAudio(string segmentId, string audioFilePath);
        void PlaySegmentAudio(string segmentId, string audioFilePath, double segmentStartTime, double playheadPosition, float volume, bool forceResync = false);
        void StopSegmentAudio();
        void PlayBgmAudio(string audioFilePath, double playheadPosition, float volume, double totalDuration, double fadeInSeconds, double fadeOutSeconds, bool forceResync = false);
        void StopBgmAudio();
    }
}