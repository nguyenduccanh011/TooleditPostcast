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
        private double _actualDurationSeconds; // Actual duration from reading samples (more accurate than metadata)
        private long _totalSampleCount; // Total samples in file for accurate position tracking
        private WaveFormat? _waveFormat; // Cached wave format for calculations

        public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        public event EventHandler<EventArgs>? PlaybackStarted;
        public event EventHandler<EventArgs>? PlaybackPaused;

        public AudioService()
        {
            // Reduce buffer latency to 50ms for more responsive playback
            // Commercial apps typically use 50-100ms for good balance
            _wavePlayer = new WaveOutEvent 
            { 
                DesiredLatency = 50,  // 50ms buffer for responsive feel while preventing dropouts
                NumberOfBuffers = 3   // More buffers = more stable but slightly higher latency
            };
            _wavePlayer.PlaybackStopped += OnPlaybackStopped;
            Log.Information("AudioService initialized with 50ms latency, 3 buffers");
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

                    // Stop playback before replacing the reader (avoid playing disposed stream)
                    try
                    {
                        _wavePlayer?.Stop();
                        if (_audioFileReader != null)
                            _audioFileReader.CurrentTime = TimeSpan.Zero;
                    }
                    catch { /* ignore */ }

                    // Cleanup previous audio
                    _audioFileReader?.Dispose();
                    _sampleAggregator = null;

                    // Load new audio file
                    _audioFileReader = new AudioFileReader(filePath);
                    _currentAudioPath = filePath;

                    var metadataDuration = _audioFileReader.TotalTime;
                    _waveFormat = _audioFileReader.WaveFormat;
                    
                    // Count actual samples from THIS EXACT reader instance.
                    // This guarantees _totalSampleCount matches what this reader produces during playback,
                    // eliminating VBR MP3 discrepancies between separate reader instances.
                    long totalSamples = 0;
                    var countBuffer = new float[8192];
                    int countRead;
                    while ((countRead = _audioFileReader.Read(countBuffer, 0, countBuffer.Length)) > 0)
                    {
                        totalSamples += countRead;
                    }
                    
                    // Reset reader to beginning for playback
                    _audioFileReader.Position = 0;
                    
                    _totalSampleCount = totalSamples;
                    _actualDurationSeconds = (double)totalSamples / (_waveFormat.SampleRate * _waveFormat.Channels);
                    
                    // Now init the player chain AFTER counting (reader is at position 0)
                    _sampleAggregator = new SampleAggregator(_audioFileReader);
                    _wavePlayer?.Init(_sampleAggregator);
                    
                    // Use actual duration for accurate timeline sync
                    var duration = TimeSpan.FromSeconds(_actualDurationSeconds);

                    var metadata = new AudioMetadata
                    {
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        Duration = duration,
                        SampleRate = _waveFormat.SampleRate,
                        Channels = _waveFormat.Channels,
                        CreatedAt = DateTime.Now
                    };

                    Log.Information("Audio loaded: {FileName}, MetadataDuration: {MetaDur}s, ActualDuration: {ActualDur}s, TotalSamples: {Samples}, SR: {SampleRate}Hz, Channels: {Channels}",
                        metadata.FileName, metadataDuration.TotalSeconds, duration.TotalSeconds, totalSamples, metadata.SampleRate, metadata.Channels);
                    
                    if (Math.Abs(metadataDuration.TotalSeconds - duration.TotalSeconds) > 0.5)
                    {
                        Log.Warning("Duration mismatch detected (likely VBR MP3): Metadata={Meta}s, Actual={Actual}s, Δ={Delta:F2}s - Using sample-based timing",
                            metadataDuration.TotalSeconds, duration.TotalSeconds, 
                            Math.Abs(metadataDuration.TotalSeconds - duration.TotalSeconds));
                    }

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
                // Reset sample counter to 0 (beginning of file)
                _sampleAggregator?.SetPlayedSamples(0);

                Log.Information("Playback stopped");
            }
        }

        /// <summary>
        /// Seek to a specific position (in seconds).
        /// Note: For VBR MP3, seek position may have small inaccuracy due to NAudio byte-based seeking.
        /// </summary>
        public void Seek(double positionSeconds)
        {
            if (_audioFileReader == null)
                throw new InvalidOperationException("No audio loaded.");

            // Use actual duration (sample-counted) for clamping, not metadata TotalTime which can be wrong for VBR MP3
            double maxDuration = _actualDurationSeconds > 0 ? _actualDurationSeconds : _audioFileReader.TotalTime.TotalSeconds;
            positionSeconds = Math.Clamp(positionSeconds, 0, maxDuration);
            var position = TimeSpan.FromSeconds(positionSeconds);
            _audioFileReader.CurrentTime = position;
            _sampleAggregator?.Reset();
            
            // Sync the sample counter to match the seek position.
            // This ensures GetCurrentPosition() returns accurate values after seek.
            if (_waveFormat != null && _sampleAggregator != null)
            {
                long seekSampleCount = (long)(positionSeconds * _waveFormat.SampleRate * _waveFormat.Channels);
                _sampleAggregator.SetPlayedSamples(seekSampleCount);
            }
            
            Log.Debug("Seek to {Position}s (sample counter synced)", positionSeconds);
        }

        /// <summary>
        /// Get current playback position in seconds.
        /// Uses SampleAggregator's played sample counter for accuracy.
        /// This counts the exact samples fed to WaveOutEvent, which is more reliable than
        /// AudioFileReader.Position (which can drift for VBR MP3 due to byte-to-time mapping).
        /// Optimized for frequent calls (30-60fps UI updates).
        /// </summary>
        public double GetCurrentPosition()
        {
            if (_waveFormat == null)
                return 0;
            
            // PRIMARY: Use the sample counter from SampleAggregator.
            // This tracks exactly how many samples have been read for playback,
            // giving a position that perfectly aligns with what the audio hardware is playing.
            if (_sampleAggregator != null)
            {
                long playedSamples = _sampleAggregator.PlayedSampleCount;
                double position = (double)playedSamples / (_waveFormat.SampleRate * _waveFormat.Channels);
                
                // Clamp to actual duration to prevent overshoot
                if (_actualDurationSeconds > 0 && position > _actualDurationSeconds)
                    return _actualDurationSeconds;
                
                return position;
            }
            
            // Fallback to CurrentTime (only if SampleAggregator is not yet initialized)
            return _audioFileReader?.CurrentTime.TotalSeconds ?? 0;
        }

        /// <summary>
        /// Get total audio duration in seconds (actual duration, not metadata).
        /// </summary>
        public double GetDuration()
        {
            // Return actual duration calculated during load (more accurate than metadata)
            return _actualDurationSeconds > 0 ? _actualDurationSeconds : (_audioFileReader?.TotalTime.TotalSeconds ?? 0);
        }
        
        /// <summary>
        /// Check if playback has reached or exceeded total duration.
        /// Useful for timer callbacks to detect end-of-track.
        /// </summary>
        public bool IsAtEnd()
        {
            if (_audioFileReader == null || _actualDurationSeconds <= 0)
                return false;
            return GetCurrentPosition() >= _actualDurationSeconds;
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
        /// Get peak samples for waveform display (e.g. timeline audio track).
        /// Uses a separate reader so playback position is not affected.
        /// Returns one peak per "bin"; values are normalized 0..1.
        /// FIXED: Uses actual sample count for accurate sync with playback.
        /// </summary>
        public float[] GetPeakSamples(int binCount)
        {
            if (string.IsNullOrEmpty(_currentAudioPath) || !File.Exists(_currentAudioPath) || binCount <= 0)
                return Array.Empty<float>();

            try
            {
                using var reader = new AudioFileReader(_currentAudioPath);
                
                // Count actual samples
                long actualTotalSamples = 0;
                var countBuffer = new float[8192];
                int countRead;
                while ((countRead = reader.Read(countBuffer, 0, countBuffer.Length)) > 0)
                {
                    actualTotalSamples += countRead;
                }

                // Reset for second pass
                reader.Position = 0;
                
                var peaks = new float[binCount];
                var buffer = new float[4096];
                int binIndex = 0;
                float maxInBin = 0f;
                long totalRead = 0;
                long samplesPerBin = Math.Max(1, actualTotalSamples / binCount);

                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0 && binIndex < binCount)
                {
                    for (int i = 0; i < read; i++)
                    {
                        float abs = Math.Abs(buffer[i]);
                        if (abs > maxInBin)
                            maxInBin = abs;
                        totalRead++;
                        if (totalRead >= samplesPerBin)
                        {
                            peaks[binIndex++] = maxInBin;
                            maxInBin = 0f;
                            totalRead = 0;
                        }
                    }
                }
                if (binIndex < binCount && maxInBin > 0)
                    peaks[binIndex] = maxInBin;

                // Do not normalize: keep raw scale so different files look different
                // (quiet file = smaller bars, loud file = taller bars). View clamps to 1.0 when drawing.
                return peaks;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not compute peak samples for waveform");
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

