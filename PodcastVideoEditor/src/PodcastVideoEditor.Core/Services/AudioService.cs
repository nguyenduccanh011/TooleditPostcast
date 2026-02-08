using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for audio operations: load, play, pause, seek, and FFT data extraction.
    /// </summary>
    public class AudioService : IDisposable
    {
        private IWavePlayer? _wavePlayer;
        private string? _currentAudioPath;
        private AudioFileReader? _audioFileReader;
        private SampleAggregator? _sampleAggregator;

        public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        public event EventHandler<EventArgs>? PlaybackStarted;
        public event EventHandler<EventArgs>? PlaybackPaused;

        public AudioService()
        {
            _wavePlayer = new WaveOutEvent();
            _wavePlayer.PlaybackStopped += OnPlaybackStopped;
            Log.Information("AudioService initialized");
        }

        /// <summary>
        /// Load audio file and return metadata.
        /// </summary>
        public async Task<AudioMetadata> LoadAudioAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(filePath))
                        throw new FileNotFoundException($"Audio file not found: {filePath}");

                    // Cleanup previous audio
                    _audioFileReader?.Dispose();

                    // Load new audio file
                    _audioFileReader = new AudioFileReader(filePath);
                    _sampleAggregator = new SampleAggregator(_audioFileReader);
                    _wavePlayer?.Init(_sampleAggregator);
                    _currentAudioPath = filePath;

                    var duration = _audioFileReader.TotalTime;
                    var waveFormat = _audioFileReader.WaveFormat;

                    var metadata = new AudioMetadata
                    {
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        Duration = duration,
                        SampleRate = waveFormat.SampleRate,
                        Channels = waveFormat.Channels,
                        CreatedAt = DateTime.Now
                    };

                    Log.Information("Audio loaded: {FileName}, Duration: {Duration}s, SR: {SampleRate}Hz, Channels: {Channels}",
                        metadata.FileName, duration.TotalSeconds, metadata.SampleRate, metadata.Channels);

                    return metadata;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to load audio: {FilePath}", filePath);
                    throw;
                }
            });
        }

        /// <summary>
        /// Play the loaded audio.
        /// </summary>
        public void Play()
        {
            if (_wavePlayer == null)
                throw new InvalidOperationException("No audio loaded. Call LoadAudioAsync first.");

            if (_wavePlayer.PlaybackState != PlaybackState.Playing)
            {
                _wavePlayer.Play();
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
                Log.Information("Playback started");
            }
        }

        /// <summary>
        /// Pause playback.
        /// </summary>
        public void Pause()
        {
            if (_wavePlayer?.PlaybackState == PlaybackState.Playing)
            {
                _wavePlayer.Pause();
                PlaybackPaused?.Invoke(this, EventArgs.Empty);
                Log.Information("Playback paused");
            }
        }

        /// <summary>
        /// Stop playback and reset position.
        /// </summary>
        public void Stop()
        {
            if (_wavePlayer?.PlaybackState != PlaybackState.Stopped)
            {
                _wavePlayer?.Stop();
                if (_audioFileReader != null)
                    _audioFileReader.CurrentTime = TimeSpan.Zero;

                Log.Information("Playback stopped");
            }
        }

        /// <summary>
        /// Seek to a specific position (in seconds).
        /// </summary>
        public void Seek(double positionSeconds)
        {
            if (_audioFileReader == null)
                throw new InvalidOperationException("No audio loaded.");

            var position = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, _audioFileReader.TotalTime.TotalSeconds));
            _audioFileReader.CurrentTime = position;
            _sampleAggregator?.Reset();
            Log.Debug("Seek to {Position}s", positionSeconds);
        }

        /// <summary>
        /// Get current playback position in seconds.
        /// </summary>
        public double GetCurrentPosition()
        {
            return _audioFileReader?.CurrentTime.TotalSeconds ?? 0;
        }

        /// <summary>
        /// Get total audio duration in seconds.
        /// </summary>
        public double GetDuration()
        {
            return _audioFileReader?.TotalTime.TotalSeconds ?? 0;
        }

        /// <summary>
        /// Get current playback state.
        /// </summary>
        public PlaybackState GetPlaybackState()
        {
            return _wavePlayer?.PlaybackState ?? PlaybackState.Stopped;
        }

        /// <summary>
        /// Set playback volume (0.0 to 1.0).
        /// </summary>
        public void SetVolume(float volume)
        {
            if (_audioFileReader != null)
            {
                _audioFileReader.Volume = Math.Clamp(volume, 0f, 1f);
                Log.Debug("Volume set to {Volume}", volume);
            }
        }

        /// <summary>
        /// Get current volume level (0.0 to 1.0).
        /// </summary>
        public float GetVolume()
        {
            return _audioFileReader?.Volume ?? 1f;
        }

        /// <summary>
        /// Extract FFT data for visualizer (simple spectrum analysis).
        /// Returns normalized float array [0, 1] representing frequency magnitudes.
        /// </summary>
        public float[] GetFFTData(int fftSize = 1024)
        {
            if (_sampleAggregator == null)
                return Array.Empty<float>();

            try
            {
                return _sampleAggregator.GetFFTData(fftSize);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting FFT data");
                return Array.Empty<float>();
            }
        }

        /// <summary>
        /// Get list of supported audio formats.
        /// </summary>
        public static string[] GetSupportedFormats()
        {
            return new[] { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma" };
        }

        public PlaybackState PlaybackState => _wavePlayer?.PlaybackState ?? PlaybackState.Stopped;
        public bool IsPlaying => PlaybackState == PlaybackState.Playing;
        public string? CurrentAudioPath => _currentAudioPath;

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Log.Information("Playback stopped (event)");
            PlaybackStopped?.Invoke(this, new PlaybackStoppedEventArgs());
        }

        public void Dispose()
        {
            _wavePlayer?.Dispose();
            _audioFileReader?.Dispose();
            _sampleAggregator = null;
            Log.Information("AudioService disposed");
        }
    }

    public class PlaybackStoppedEventArgs : EventArgs
    {
    }
}

