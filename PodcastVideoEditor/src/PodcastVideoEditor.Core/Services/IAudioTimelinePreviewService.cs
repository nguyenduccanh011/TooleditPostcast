using System;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Timeline preview contract for segment audio sync.
    /// Keeps timeline preview orchestration independent from AudioService implementation details.
    /// </summary>
    public interface IAudioTimelinePreviewService : IAudioPlaybackService
    {
        string? CurrentAudioPath { get; }
        string? CurrentSegmentId { get; }

        float[] GetPeakSamples(int binCount);
        float[] GetFFTData(int fftSize = 1024);
        void PreloadSegmentAudio(string segmentId, string audioFilePath);
        void PlaySegmentAudio(string segmentId, string audioFilePath, double segmentStartTime, double playheadPosition, float volume, double sourceStartOffset = 0, bool forceResync = false);
        void StopSegmentAudio();
    }
}