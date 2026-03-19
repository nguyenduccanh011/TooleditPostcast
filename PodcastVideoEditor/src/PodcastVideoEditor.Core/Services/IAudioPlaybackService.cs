using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using System;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Narrow transport-facing contract for audio playback orchestration.
    /// Keeps UI playback coordination testable without depending on concrete NAudio wiring.
    /// </summary>
    public interface IAudioPlaybackService : IDisposable
    {
        event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        event EventHandler<EventArgs>? PlaybackStarted;
        event EventHandler<EventArgs>? PlaybackPaused;

        Task<AudioMetadata> LoadAudioAsync(string filePath);
        void Play();
        void Pause();
        void Stop();
        void Seek(double positionSeconds);
        double GetCurrentPosition();
        double GetDuration();
        void SetVolume(float volume);
        float GetVolume();

        PlaybackState PlaybackState { get; }
        bool IsPlaying { get; }
    }
}