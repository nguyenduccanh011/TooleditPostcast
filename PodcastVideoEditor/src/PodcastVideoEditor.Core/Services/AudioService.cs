using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for audio operations: load, play, pause, seek, and FFT data extraction.
    /// </summary>
    public class AudioService : IDisposable
    {
        private IWavePlayer? _wavePlayer;
        private string? _sourceAudioPath;
        private string? _playbackAudioPath;
        private AudioFileReader? _audioFileReader;
        private SampleAggregator? _sampleAggregator;
        private double _actualDurationSeconds; // Actual duration from reading samples (more accurate than metadata)
        private long _totalSampleCount; // Total samples in file for accurate position tracking
        private WaveFormat? _waveFormat; // Cached wave format for calculations
        private string? _decodedAudioPath;
        private bool _isDecodedCache;

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
                    bool sameSource = string.Equals(_sourceAudioPath, filePath, StringComparison.OrdinalIgnoreCase);
                    if (!sameSource)
                        CleanupDecodedCache();

                    // Load new audio file (decode M4A/AAC to WAV cache for accurate seek)
                    string playbackPath = GetPlaybackPath(filePath);
                    _audioFileReader = new AudioFileReader(playbackPath);
                    _sourceAudioPath = filePath;
                    _playbackAudioPath = playbackPath;

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

        private string GetPlaybackPath(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".m4a" && ext != ".aac")
                return filePath;

            string cachePath = GetDecodedCachePath(filePath);
            var sourceInfo = new FileInfo(filePath);
            var cacheInfo = new FileInfo(cachePath);

            if (cacheInfo.Exists && cacheInfo.Length > 0 && cacheInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
            {
                _decodedAudioPath = cachePath;
                _isDecodedCache = true;
                Log.Information("Using cached WAV for accurate seek: {Path}", cachePath);
                return cachePath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? Path.GetTempPath());
            var sw = Stopwatch.StartNew();
            using (var reader = new AudioFileReader(filePath))
            {
                WaveFileWriter.CreateWaveFile(cachePath, reader);
            }
            sw.Stop();

            _decodedAudioPath = cachePath;
            _isDecodedCache = true;
            Log.Information("Decoded WAV cache created in {Ms} ms: {Path}", sw.ElapsedMilliseconds, cachePath);
            return cachePath;
        }

        private static string GetDecodedCachePath(string filePath)
        {
            string hash = ComputeHash(filePath);
            string fileName = $"audio-cache-{hash}.wav";
            string cacheDir = Path.Combine(Path.GetTempPath(), "PodcastVideoEditor", "AudioCache");
            return Path.Combine(cacheDir, fileName);
        }

        private static string ComputeHash(string input)
        {
            using var sha = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void CleanupDecodedCache()
        {
            if (!_isDecodedCache || string.IsNullOrEmpty(_decodedAudioPath))
                return;

            try
            {
                if (File.Exists(_decodedAudioPath))
                    File.Delete(_decodedAudioPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete decoded WAV cache: {Path}", _decodedAudioPath);
            }
            finally
            {
                _decodedAudioPath = null;
                _isDecodedCache = false;
            }
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
            double clampedPosition = Math.Clamp(positionSeconds, 0, maxDuration);

            bool wasPlaying = _wavePlayer?.PlaybackState == PlaybackState.Playing;
            if (wasPlaying)
                _wavePlayer?.Pause();

            // Seek to coarse position first (decoder may snap to keyframes)
            _audioFileReader.CurrentTime = TimeSpan.FromSeconds(clampedPosition);
            double actualReaderTime = _audioFileReader.CurrentTime.TotalSeconds;

            // Accurate seek on-demand: decode and discard samples to reach exact target time
            if (_waveFormat != null)
            {
                double deltaSeconds = clampedPosition - actualReaderTime;
                if (Math.Abs(deltaSeconds) > 0.02)
                {
                    if (deltaSeconds < 0)
                    {
                        // Rewind slightly and then skip forward to the exact point
                        double rewindTime = Math.Max(0, clampedPosition - 1.0);
                        _audioFileReader.CurrentTime = TimeSpan.FromSeconds(rewindTime);
                        actualReaderTime = _audioFileReader.CurrentTime.TotalSeconds;
                        deltaSeconds = clampedPosition - actualReaderTime;
                    }

                    if (deltaSeconds > 0)
                    {
                        int sampleRate = _waveFormat.SampleRate;
                        int channels = _waveFormat.Channels;
                        long samplesToSkip = (long)(deltaSeconds * sampleRate * channels);
                        long skipped = 0;
                        var skipBuffer = new float[8192];

                        while (skipped < samplesToSkip)
                        {
                            int toRead = (int)Math.Min(skipBuffer.Length, samplesToSkip - skipped);
                            int read = _audioFileReader.Read(skipBuffer, 0, toRead);
                            if (read <= 0)
                                break;
                            skipped += read;
                        }
                    }
                }
            }

            _sampleAggregator?.Reset();
            if (_waveFormat != null && _sampleAggregator != null)
            {
                long seekSampleCount = (long)(clampedPosition * _waveFormat.SampleRate * _waveFormat.Channels);
                _sampleAggregator.SetPlayedSamples(seekSampleCount);
            }

            if (wasPlaying)
                _wavePlayer?.Play();

            Log.Debug("Seek to {Position}s (accurate seek on-demand)", clampedPosition);
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
            if (_waveFormat == null || _audioFileReader == null)
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
            try
            {
                return _audioFileReader.CurrentTime.TotalSeconds;
            }
            catch
            {
                return 0;
            }
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
            if (string.IsNullOrEmpty(_playbackAudioPath) || !File.Exists(_playbackAudioPath) || binCount <= 0)
                return Array.Empty<float>();

            try
            {
                using var reader = new AudioFileReader(_playbackAudioPath);
                var peaks = new float[binCount];
                var buffer = new float[4096];
                long totalSamplesEstimate = _totalSampleCount;
                if (totalSamplesEstimate <= 0)
                {
                    var totalTimeSeconds = Math.Max(0.001, reader.TotalTime.TotalSeconds);
                    totalSamplesEstimate = (long)(totalTimeSeconds * reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
                }
                totalSamplesEstimate = Math.Max(totalSamplesEstimate, 1);
                long globalSampleIndex = 0;

                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        int binIndex = (int)Math.Min(binCount - 1, (globalSampleIndex * binCount) / totalSamplesEstimate);
                        float abs = Math.Abs(buffer[i]);
                        if (abs > peaks[binIndex])
                            peaks[binIndex] = abs;
                        globalSampleIndex++;
                    }
                }

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
        public string? CurrentAudioPath => _sourceAudioPath;

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
            CleanupDecodedCache();
            Log.Information("AudioService disposed");
        }
    }

    public class PlaybackStoppedEventArgs : EventArgs
    {
    }
}

