using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
    public class AudioService : IAudioTimelinePreviewService, IDisposable
    {
        // ── Timing / threshold constants (named to avoid magic numbers) ──────────
        private const double SeekResyncThresholdSeconds = 0.02;
        private const double SegmentOffsetSkipThresholdSeconds = 0.05;
        private const double SeekSafetyMarginSeconds = 0.01;
        private const int DesiredLatencyMs = 50;
        private const int BufferCount = 3;

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

        /// <summary>
        /// When true, the next WaveOutEvent PlaybackStopped event is suppressed
        /// (not forwarded to subscribers). Used during Seek-from-Paused to flush
        /// stale device buffers via Stop+Init without triggering coordinator position reset.
        /// </summary>
        private volatile bool _suppressNextStopEvent;

        /// <summary>
        /// Set by Seek() when the device was Paused. Cleared by Play() after flushing.
        /// Deferring the flush to Play() avoids expensive Stop+Init on every scrub drag.
        /// </summary>
        private volatile bool _needsBufferFlush;

        /// <summary>
        /// Guards all mixer add/remove/dispose operations.
        /// NAudio's MixingSampleProvider uses an unsynchronized List&lt;ISampleProvider&gt; internally;
        /// concurrent AddMixerInput/RemoveMixerInput from the UI-thread sync loop and background
        /// seek/load operations would corrupt the list. This lock serializes access.
        /// </summary>
        private readonly object _mixerLock = new();

        public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        public event EventHandler<EventArgs>? PlaybackStarted;
        public event EventHandler<EventArgs>? PlaybackPaused;

        public AudioService()
        {
            // Reduce buffer latency to 50ms for more responsive playback
            // Commercial apps typically use 50-100ms for good balance
            _wavePlayer = new WaveOutEvent 
            { 
                DesiredLatency = DesiredLatencyMs,
                NumberOfBuffers = BufferCount
            };
            _wavePlayer.PlaybackStopped += OnPlaybackStopped;
            Log.Information("AudioService initialized with {Latency}ms latency, {Buffers} buffers", DesiredLatencyMs, BufferCount);
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

                    // Cleanup previous audio (including any active segment/BGM mixer inputs)
                    StopSegmentAudio();
                    StopBgmAudio();
                    _audioFileReader?.Dispose();
                    _sampleAggregator = null;
                    lock (_mixerLock) { _mixer = null; }
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
                    // Single-mixer architecture: all audio (primary + segment + BGM) goes through
                    // one MixingSampleProvider → one WaveOutEvent, guaranteeing zero inter-stream offset.
                    _sampleAggregator = new SampleAggregator(_audioFileReader);
                    lock (_mixerLock)
                    {
                        _mixer = new MixingSampleProvider(new[] { (ISampleProvider)_sampleAggregator }) { ReadFully = true };
                    }
                    _wavePlayer?.Init(_mixer);
                    
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

        private static string ComputeHash(string filePath)
        {
            // Hash path + last-write-time + file-size so cache invalidates when file content changes
            var info = new FileInfo(filePath);
            string key = $"{filePath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            using var sha = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(key);
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

            if (_audioFileReader == null)
                throw new InvalidOperationException("No audio loaded. Call LoadAudioAsync first.");

            if (_wavePlayer.PlaybackState != PlaybackState.Playing)
            {
                if (IsAtEnd())
                {
                    Log.Information("Play() detected IsAtEnd — resetting to beginning (pos={Pos}s, dur={Dur}s, exhausted={Exhausted})",
                        GetCurrentPosition(), _actualDurationSeconds, _sampleAggregator?.IsSourceExhausted);
                    _audioFileReader.CurrentTime = TimeSpan.Zero;
                    _sampleAggregator?.Reset();
                    _sampleAggregator?.SetPlayedSamples(0);
                }
                else
                {
                    Log.Information("Play() NOT at end — resuming (pos={Pos}s, dur={Dur}s, exhausted={Exhausted}, state={State}, samples={Samples}/{Total}, needsFlush={Flush})",
                        GetCurrentPosition(), _actualDurationSeconds, _sampleAggregator?.IsSourceExhausted,
                        _wavePlayer.PlaybackState, _sampleAggregator?.PlayedSampleCount, _totalSampleCount, _needsBufferFlush);
                }

                // ── Flush stale WaveOutEvent device buffers (deferred from Seek) ──
                // When WaveOutEvent is Paused it holds pre-queued audio buffers. After a Seek
                // repositions the source, those stale buffers would play first, causing
                // _playedSampleCount to advance ahead of what the device actually outputs.
                // We flush once here (Stop+Init) instead of in every Seek() call to avoid
                // jitter during timeline scrubbing.
                if (_needsBufferFlush && _wavePlayer.PlaybackState == PlaybackState.Paused)
                {
                    Log.Information("Play() flushing stale buffers before resume (Paused→Stop→Init→Play)");
                    _suppressNextStopEvent = true;
                    _needsBufferFlush = false;
                    _wavePlayer.Stop();
                    lock (_mixerLock)
                    {
                        if (_mixer != null)
                            _wavePlayer.Init(_mixer);
                    }
                }

                // Fire event BEFORE starting playback so subscribers can add mixer inputs
                // (segment + BGM). The first mixer Read() then includes all sources = zero offset.
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
                _wavePlayer.Play();
                Log.Information("Playback started from {Pos}s (state was {State})",
                    GetCurrentPosition(), _wavePlayer.PlaybackState);
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
                // Mixer pauses all inputs (segment + BGM) automatically
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
            StopSegmentAudio();
            StopBgmAudio();
        }

        /// <summary>
        /// Seek to a specific position (in seconds).
        /// For PCM/WAV files (including our decoded cache), byte-seek is sample-accurate
        /// so we skip the expensive sample-scan path entirely.
        /// For VBR MP3/AAC (rare — only non-cached files), we decode-and-skip to reach exact position.
        /// </summary>
        public void Seek(double positionSeconds)
        {
            if (_audioFileReader == null)
                throw new InvalidOperationException("No audio loaded.");

            // Use actual duration (sample-counted) for clamping, not metadata TotalTime which can be wrong for VBR MP3
            double maxDuration = _actualDurationSeconds > 0 ? _actualDurationSeconds : _audioFileReader.TotalTime.TotalSeconds;
            double clampedPosition = Math.Clamp(positionSeconds, 0, maxDuration);

            bool wasPlaying = _wavePlayer?.PlaybackState == PlaybackState.Playing;
            bool wasPaused = _wavePlayer?.PlaybackState == PlaybackState.Paused;
            if (wasPlaying)
                _wavePlayer?.Pause();

            Log.Debug("Seek({Pos}s): wasPlaying={WasPlaying}, wasPaused={WasPaused}, exhausted={Exhausted}, playedSamples={Samples}",
                positionSeconds, wasPlaying, wasPaused,
                _sampleAggregator?.IsSourceExhausted, _sampleAggregator?.PlayedSampleCount);

            // Seek to coarse position first (decoder may snap to keyframes)
            _audioFileReader.CurrentTime = TimeSpan.FromSeconds(clampedPosition);
            double actualReaderTime = _audioFileReader.CurrentTime.TotalSeconds;

            // PCM/WAV files have exact byte-aligned seek — no sample-skip needed.
            // Only do the expensive sample-scan for compressed non-cached formats.
            bool isLikelyCbr = IsPlaybackFilePcmOrWav();
            if (!isLikelyCbr && _waveFormat != null)
            {
                double deltaSeconds = clampedPosition - actualReaderTime;
                if (Math.Abs(deltaSeconds) > SeekResyncThresholdSeconds)
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
                // Use Math.Round instead of truncation to prevent being 1 sample short
                // at EOF. Clamp to _totalSampleCount to never overshoot.
                long seekSampleCount = Math.Min(
                    (long)Math.Round(clampedPosition * _waveFormat.SampleRate * _waveFormat.Channels),
                    _totalSampleCount);
                _sampleAggregator.SetPlayedSamples(seekSampleCount);
                Log.Debug("Seek: samples={Samples}/{Total}, exhausted cleared, pos={Pos}s",
                    seekSampleCount, _totalSampleCount, clampedPosition);
            }

            // Note: segment and BGM readers are resynced by the caller
            // (SeekTo → UpdateSegmentAudioPlayback with forceResync=true)
            // after the mixer is resumed, so they hear the correct position.

            // Mark that a seek happened while paused — Play() will flush stale device
            // buffers once, right before resuming. Flushing here (every Seek) would
            // cause jitter during timeline scrubbing because Stop+Init is expensive.
            if (wasPaused)
                _needsBufferFlush = true;

            if (wasPlaying)
                _wavePlayer?.Play();

            Log.Debug("Seek to {Position}s completed (wasPaused={WasPaused}, wasPlaying={WasPlaying}, needsFlush={Flush})",
                clampedPosition, wasPaused, wasPlaying, _needsBufferFlush);
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
                // When source is exhausted, we are definitively at EOF — return exact duration
                // to avoid floating-point truncation causing IsAtEnd() to return false.
                if (_sampleAggregator.IsSourceExhausted && _actualDurationSeconds > 0)
                    return _actualDurationSeconds;

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
        /// Uses both sample-count position AND the source-exhausted flag from SampleAggregator.
        /// The flag provides definitive EOF detection even when floating-point truncation
        /// causes the sample count to be 1 frame short of _actualDurationSeconds.
        /// </summary>
        public bool IsAtEnd()
        {
            if (_audioFileReader == null || _actualDurationSeconds <= 0)
                return false;
            if (_sampleAggregator?.IsSourceExhausted == true)
                return true;
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

            return ComputePeaks(_playbackAudioPath, binCount, _totalSampleCount);
        }

        /// <summary>
        /// Generate peak samples for any audio file (static, for waveform display in segments).
        /// </summary>
        public static float[] GetPeakSamplesFromFile(string filePath, int binCount)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || binCount <= 0)
                return Array.Empty<float>();

            return ComputePeaks(filePath, binCount, totalSampleCountHint: 0);
        }

        /// <summary>
        /// Shared peak computation: read the entire audio file and bucket max-abs into bins.
        /// </summary>
        private static float[] ComputePeaks(string filePath, int binCount, long totalSampleCountHint)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                var peaks = new float[binCount];
                var buffer = new float[4096];
                long totalSamplesEstimate = totalSampleCountHint;
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

                return peaks;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not compute peak samples for: {Path}", filePath);
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

        /// <summary>
        /// Returns true if the playback file is PCM / WAV — for which byte-seek is sample-accurate
        /// and the expensive decode-and-skip path in Seek() can be skipped.
        /// This is true whenever _isDecodedCache is set (M4A/AAC decoded to temp WAV)
        /// or when the source file itself is a WAV.
        /// </summary>
        private bool IsPlaybackFilePcmOrWav()
        {
            if (_isDecodedCache)
                return true;
            var ext = Path.GetExtension(_playbackAudioPath);
            return string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase);
        }

        // ======== Single-mixer architecture: all audio through one WaveOutEvent ========
        // MixingSampleProvider routes primary + segment + BGM through one output clock,
        // guaranteeing zero inter-stream timing offset.
        private MixingSampleProvider? _mixer;

        // Segment audio (dynamically added/removed from mixer)
        private ISampleProvider? _segmentMixerInput;
        private AudioFileReader? _segmentReader;
        private string? _currentSegmentAudioPath;
        private string? _currentSegmentId;

        // Pre-loaded segment reader for near-zero latency (no WaveOutEvent needed with mixer)
        private AudioFileReader? _preloadedReader;
        private string? _preloadedSegmentId;
        private string? _preloadedAudioPath;

        /// <summary>
        /// Pre-load audio for an upcoming segment so playback can start with minimal latency.
        /// Call this when the playhead is approaching a segment boundary.
        /// </summary>
        public void PreloadSegmentAudio(string segmentId, string audioFilePath)
        {
            if (_preloadedSegmentId == segmentId) return;
            if (_currentSegmentId == segmentId) return;
            if (!File.Exists(audioFilePath)) return;

            DisposePreloadedSegment();

            try
            {
                _preloadedReader = new AudioFileReader(audioFilePath);
                _preloadedSegmentId = segmentId;
                _preloadedAudioPath = audioFilePath;
                Log.Debug("Segment audio preloaded: {SegmentId}", segmentId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to preload segment audio: {Path}", audioFilePath);
                DisposePreloadedSegment();
            }
        }

        private void DisposePreloadedSegment()
        {
            try { _preloadedReader?.Dispose(); } catch { }
            _preloadedReader = null;
            _preloadedSegmentId = null;
            _preloadedAudioPath = null;
        }

        /// <summary>
        /// Play audio for a specific segment. Adds to mixer for zero-offset sync.
        /// If already playing this segment, adjusts volume only (mixer keeps position in sync).
        /// If different, swaps mixer input.
        /// Set forceResync=true after an explicit Seek() to resync the reader position.
        /// </summary>
        public void PlaySegmentAudio(string segmentId, string audioFilePath, double segmentStartTime, double playheadPosition, float volume, bool forceResync = false)
        {
            if (!File.Exists(audioFilePath))
                return;
            lock (_mixerLock)
            {
                if (_mixer == null) return;
            }

            double offsetInSegment = Math.Max(0, playheadPosition - segmentStartTime);

            // Same segment already in mixer — adjust volume; only resync position on explicit seek
            if (_currentSegmentId == segmentId && _segmentReader != null)
            {
                _segmentReader.Volume = Math.Clamp(volume, 0f, 1f);
                if (forceResync)
                {
                    double expectedOffset = Math.Max(0, playheadPosition - segmentStartTime);
                    var seekTarget = TimeSpan.FromSeconds(
                        Math.Min(expectedOffset, _segmentReader.TotalTime.TotalSeconds - SeekSafetyMarginSeconds));
                    if (seekTarget >= TimeSpan.Zero)
                        _segmentReader.CurrentTime = seekTarget;
                }
                return;
            }

            // Different segment — remove old from mixer and add new
            StopSegmentAudio();

            try
            {
                // Use preloaded reader if available
                if (_preloadedSegmentId == segmentId && _preloadedReader != null)
                {
                    _segmentReader = _preloadedReader;
                    _preloadedReader = null;
                    _preloadedSegmentId = null;
                    _preloadedAudioPath = null;
                }
                else
                {
                    DisposePreloadedSegment();
                    _segmentReader = new AudioFileReader(audioFilePath);
                }

                _segmentReader.Volume = Math.Clamp(volume, 0f, 1f);
                if (offsetInSegment > SegmentOffsetSkipThresholdSeconds)
                {
                    var targetTime = TimeSpan.FromSeconds(Math.Min(offsetInSegment, _segmentReader.TotalTime.TotalSeconds - SeekSafetyMarginSeconds));
                    if (targetTime > TimeSpan.Zero)
                        _segmentReader.CurrentTime = targetTime;
                }

                // Add to mixer (convert format if needed for sample rate / channel match)
                lock (_mixerLock)
                {
                    if (_mixer == null) { _segmentReader?.Dispose(); _segmentReader = null; return; }
                    _segmentMixerInput = ConvertToMixerFormat(_segmentReader, _mixer.WaveFormat);
                    _mixer.AddMixerInput(_segmentMixerInput);
                }

                _currentSegmentId = segmentId;
                _currentSegmentAudioPath = audioFilePath;
                Log.Debug("Segment audio added to mixer: {SegmentId} at offset {Offset}s, vol={Vol}", segmentId, offsetInSegment, volume);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to play segment audio: {Path}", audioFilePath);
                StopSegmentAudio();
            }
        }

        /// <summary>
        /// Update volume for currently playing segment audio (used for fade in/out).
        /// </summary>
        public void SetSegmentVolume(float volume)
        {
            if (_segmentReader != null)
                _segmentReader.Volume = Math.Clamp(volume, 0f, 1f);
        }

        /// <summary>
        /// Stop the currently playing segment audio.
        /// </summary>
        public void StopSegmentAudio()
        {
            var mixerInput = _segmentMixerInput;
            var reader = _segmentReader;
            _segmentMixerInput = null;
            _segmentReader = null;
            _currentSegmentId = null;
            _currentSegmentAudioPath = null;

            try { if (mixerInput != null) lock (_mixerLock) { _mixer?.RemoveMixerInput(mixerInput); } }
            catch (Exception ex) { Log.Warning(ex, "Error removing segment from mixer"); }
            try { reader?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Error disposing segment reader"); }
        }

        /// <summary>
        /// Get the ID of the currently playing segment audio, or null if none.
        /// </summary>
        public string? CurrentSegmentId => _currentSegmentId;

        // ======== BGM (Background Music) audio — added/removed from mixer ========
        private ISampleProvider? _bgmMixerInput;
        private AudioFileReader? _bgmReader;
        private string? _currentBgmPath;
        private bool _bgmIsPlaying;

        /// <summary>
        /// Start or update BGM playback for preview. Adds to mixer for zero-offset sync.
        /// During normal playback only volume is updated (mixer keeps position in sync).
        /// Set forceResync=true after an explicit Seek() to resync the reader position.
        /// </summary>
        public void PlayBgmAudio(string audioFilePath, double playheadPosition, float volume, double totalDuration,
                                  double fadeInSeconds, double fadeOutSeconds, bool forceResync = false)
        {
            if (!File.Exists(audioFilePath))
                return;
            lock (_mixerLock)
            {
                if (_mixer == null) return;
            }

            // Already playing same file — just adjust volume; only resync on explicit seek
            if (_currentBgmPath == audioFilePath && _bgmReader != null && _bgmMixerInput != null)
            {
                float effectiveVol = CalculateBgmFadeVolume(volume, playheadPosition, totalDuration, fadeInSeconds, fadeOutSeconds);
                _bgmReader.Volume = Math.Clamp(effectiveVol, 0f, 1f);

                if (forceResync)
                {
                    var seekTarget = TimeSpan.FromSeconds(
                        Math.Min(playheadPosition, _bgmReader.TotalTime.TotalSeconds - SeekSafetyMarginSeconds));
                    if (seekTarget >= TimeSpan.Zero)
                        _bgmReader.CurrentTime = seekTarget;
                }
                return;
            }

            // Different file or not yet started
            StopBgmAudio();

            try
            {
                _bgmReader = new AudioFileReader(audioFilePath);
                float effectiveVol = CalculateBgmFadeVolume(volume, playheadPosition, totalDuration, fadeInSeconds, fadeOutSeconds);
                _bgmReader.Volume = Math.Clamp(effectiveVol, 0f, 1f);

                if (playheadPosition > SegmentOffsetSkipThresholdSeconds)
                {
                    var seekTarget = TimeSpan.FromSeconds(
                        Math.Min(playheadPosition, _bgmReader.TotalTime.TotalSeconds - SeekSafetyMarginSeconds));
                    if (seekTarget > TimeSpan.Zero)
                        _bgmReader.CurrentTime = seekTarget;
                }

                lock (_mixerLock)
                {
                    if (_mixer == null) { _bgmReader?.Dispose(); _bgmReader = null; return; }
                    _bgmMixerInput = ConvertToMixerFormat(_bgmReader, _mixer.WaveFormat);
                    _mixer.AddMixerInput(_bgmMixerInput);
                }
                _currentBgmPath = audioFilePath;
                _bgmIsPlaying = true;
                Log.Debug("BGM added to mixer: {Path} at {Pos}s", audioFilePath, playheadPosition);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to play BGM audio: {Path}", audioFilePath);
                StopBgmAudio();
            }
        }

        /// <summary>Stop and remove BGM from mixer.</summary>
        public void StopBgmAudio()
        {
            var mixerInput = _bgmMixerInput;
            var reader = _bgmReader;
            _bgmMixerInput = null;
            _bgmReader = null;
            _currentBgmPath = null;
            _bgmIsPlaying = false;

            try { if (mixerInput != null) lock (_mixerLock) { _mixer?.RemoveMixerInput(mixerInput); } }
            catch (Exception ex) { Log.Warning(ex, "Error removing BGM from mixer"); }
            try { reader?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Error disposing BGM reader"); }
        }

        /// <summary>Whether BGM is currently in the mixer.</summary>
        public bool IsBgmPlaying => _bgmIsPlaying && _bgmMixerInput != null;

        private static float CalculateBgmFadeVolume(float baseVolume, double playheadPosition, double totalDuration,
                                                     double fadeInSeconds, double fadeOutSeconds)
        {
            float vol = baseVolume;
            if (fadeInSeconds > 0 && playheadPosition < fadeInSeconds)
                vol *= (float)(playheadPosition / fadeInSeconds);
            double remaining = totalDuration - playheadPosition;
            if (fadeOutSeconds > 0 && remaining < fadeOutSeconds)
                vol *= (float)(remaining / fadeOutSeconds);
            return Math.Clamp(vol, 0f, 1f);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (_suppressNextStopEvent)
            {
                Log.Debug("PlaybackStopped event suppressed (seek buffer flush)");
                _suppressNextStopEvent = false;
                return;
            }
            Log.Information("Playback stopped (event)");
            PlaybackStopped?.Invoke(this, new PlaybackStoppedEventArgs());
        }

        /// <summary>
        /// Convert an ISampleProvider to match the mixer's WaveFormat (sample rate + channels).
        /// This ensures segment/BGM audio from files with different formats can be mixed.
        /// Must be called while holding _mixerLock or with a captured WaveFormat.
        /// </summary>
        private ISampleProvider ConvertToMixerFormat(ISampleProvider source, WaveFormat target)
        {
            // Channel conversion (mono ↔ stereo)
            if (source.WaveFormat.Channels == 1 && target.Channels == 2)
                source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels == 2 && target.Channels == 1)
                source = new StereoToMonoSampleProvider(source);

            // Sample rate conversion
            if (source.WaveFormat.SampleRate != target.SampleRate)
                source = new WdlResamplingSampleProvider(source, target.SampleRate);

            return source;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            StopSegmentAudio();
            DisposePreloadedSegment();
            StopBgmAudio();
            _wavePlayer?.Dispose();
            _audioFileReader?.Dispose();
            _sampleAggregator = null;
            lock (_mixerLock) { _mixer = null; }
            CleanupDecodedCache();
            Log.Information("AudioService disposed");
        }
    }

    public class PlaybackStoppedEventArgs : EventArgs
    {
    }
}

