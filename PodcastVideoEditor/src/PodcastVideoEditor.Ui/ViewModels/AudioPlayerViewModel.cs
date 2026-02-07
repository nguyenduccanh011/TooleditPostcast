using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for audio player functionality with MVVM pattern.
    /// Handles UI state and commands for audio playback control.
    /// </summary>
    public partial class AudioPlayerViewModel : ObservableObject
    {
        private readonly AudioService _audioService;
        private System.Timers.Timer? _positionUpdateTimer;
        private AudioMetadata? _currentAudioMetadata;

        [ObservableProperty]
        private double currentPosition = 0;

        [ObservableProperty]
        private double totalDuration = 0;

        [ObservableProperty]
        private bool isPlaying = false;

        [ObservableProperty]
        private string audioFileName = "No audio loaded";

        [ObservableProperty]
        private string durationDisplay = "00:00";

        [ObservableProperty]
        private string positionDisplay = "00:00";

        [ObservableProperty]
        private bool isAudioLoaded = false;

        [ObservableProperty]
        private float volume = 1.0f;

        [ObservableProperty]
        private string statusMessage = "Ready";

        public AudioPlayerViewModel()
        {
            _audioService = new AudioService();
            _audioService.PlaybackStarted += OnPlaybackStarted;
            _audioService.PlaybackPaused += OnPlaybackPaused;
            _audioService.PlaybackStopped += OnPlaybackStopped;

            InitializePositionUpdateTimer();
            Log.Information("AudioPlayerViewModel initialized");
        }

        private void InitializePositionUpdateTimer()
        {
            _positionUpdateTimer = new System.Timers.Timer(100); // Update every 100ms
            _positionUpdateTimer.Elapsed += (sender, e) =>
            {
                CurrentPosition = _audioService.GetCurrentPosition();
                PositionDisplay = FormatTime(CurrentPosition);
            };
            _positionUpdateTimer.AutoReset = true;
        }

        /// <summary>
        /// Load an audio file asynchronously.
        /// </summary>
        [RelayCommand]
        public async Task LoadAudioAsync(string filePath)
        {
            try
            {
                StatusMessage = "Loading audio...";
                Log.Information("Loading audio from: {FilePath}", filePath);

                var metadata = await _audioService.LoadAudioAsync(filePath);
                _currentAudioMetadata = metadata;

                AudioFileName = metadata.FileName;
                TotalDuration = metadata.Duration.TotalSeconds;
                DurationDisplay = FormatTime(TotalDuration);
                IsAudioLoaded = true;
                CurrentPosition = 0;
                PositionDisplay = "00:00";

                StatusMessage = $"Loaded: {metadata.FileName} ({FormatTime(TotalDuration)})";
                Log.Information("Audio loaded successfully: {FileName}", metadata.FileName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading audio file");
                StatusMessage = $"Error: {ex.Message}";
                IsAudioLoaded = false;
            }
        }

        /// <summary>
        /// Play the loaded audio.
        /// </summary>
        [RelayCommand]
        public void Play()
        {
            try
            {
                if (!IsAudioLoaded)
                {
                    StatusMessage = "No audio loaded";
                    return;
                }

                _audioService.Play();
                _positionUpdateTimer?.Start();
                IsPlaying = true;
                StatusMessage = "Playing";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error playing audio");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Pause playback.
        /// </summary>
        [RelayCommand]
        public void Pause()
        {
            try
            {
                _audioService.Pause();
                _positionUpdateTimer?.Stop();
                IsPlaying = false;
                StatusMessage = "Paused";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error pausing audio");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Stop playback.
        /// </summary>
        [RelayCommand]
        public void Stop()
        {
            try
            {
                _audioService.Stop();
                _positionUpdateTimer?.Stop();
                IsPlaying = false;
                CurrentPosition = 0;
                PositionDisplay = "00:00";
                StatusMessage = "Stopped";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping audio");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Seek to a specific position in the audio.
        /// </summary>
        [RelayCommand]
        public void Seek(double positionSeconds)
        {
            try
            {
                if (!IsAudioLoaded)
                    return;

                _audioService.Seek(positionSeconds);
                CurrentPosition = positionSeconds;
                PositionDisplay = FormatTime(positionSeconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error seeking audio");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle volume change (0.0 to 1.0).
        /// </summary>
        partial void OnVolumeChanged(float value)
        {
            try
            {
                _audioService.SetVolume(value);
                Log.Debug("Volume changed to {Volume}", value);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error changing volume");
            }
        }

        private void OnPlaybackStarted(object? sender, EventArgs e)
        {
            IsPlaying = true;
            StatusMessage = "Playing";
            _positionUpdateTimer?.Start();
        }

        private void OnPlaybackPaused(object? sender, EventArgs e)
        {
            IsPlaying = false;
            StatusMessage = "Paused";
            _positionUpdateTimer?.Stop();
        }

        private void OnPlaybackStopped(object? sender, PodcastVideoEditor.Core.Services.PlaybackStoppedEventArgs e)
        {
            IsPlaying = false;
            StatusMessage = "Stopped";
            CurrentPosition = 0;
            PositionDisplay = "00:00";
            _positionUpdateTimer?.Stop();
        }

        private string FormatTime(double seconds)
        {
            var timespan = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (timespan.Hours > 0)
                return timespan.ToString(@"hh\:mm\:ss");
            return timespan.ToString(@"mm\:ss");
        }

        public void Dispose()
        {
            _positionUpdateTimer?.Dispose();
            _audioService?.Dispose();
        }
    }
}
