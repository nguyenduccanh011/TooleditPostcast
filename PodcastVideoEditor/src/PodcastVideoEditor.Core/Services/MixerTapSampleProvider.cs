using NAudio.Dsp;
using NAudio.Wave;
using System;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Wraps the mixer output to capture all mixed audio samples into a ring buffer
    /// for FFT extraction. Unlike SampleAggregator, this does NOT track playback position —
    /// it purely captures the post-mix audio for spectrum visualization.
    ///
    /// Pipeline position:
    ///   MixingSampleProvider → MixerTapSampleProvider → WaveOutEvent
    ///
    /// This ensures the visualizer receives FFT data from ALL audio sources
    /// (main audio + segment audio + BGM), not just the main file.
    /// </summary>
    public sealed class MixerTapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _ringBuffer;
        private readonly object _lock = new();
        private int _writeIndex;

        public MixerTapSampleProvider(ISampleProvider source, int ringBufferSize = 8192)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (ringBufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(ringBufferSize));

            _ringBuffer = new float[ringBufferSize];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);

            if (read > 0)
            {
                lock (_lock)
                {
                    for (int n = 0; n < read; n++)
                    {
                        _ringBuffer[_writeIndex] = buffer[offset + n];
                        _writeIndex = (_writeIndex + 1) % _ringBuffer.Length;
                    }
                }
            }

            return read;
        }

        /// <summary>
        /// Extract FFT magnitude spectrum from the ring buffer.
        /// Applies Hann window and NAudio's radix-2 FFT. Returns fftSize/2 magnitudes.
        /// </summary>
        public float[] GetFFTData(int fftSize)
        {
            if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of two.", nameof(fftSize));

            var complex = new Complex[fftSize];

            lock (_lock)
            {
                for (int i = 0; i < fftSize; i++)
                {
                    var index = (_writeIndex - fftSize + i);
                    if (index < 0)
                        index += _ringBuffer.Length;

                    var sample = _ringBuffer[index % _ringBuffer.Length];
                    var window = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
                    complex[i].X = sample * window;
                    complex[i].Y = 0;
                }
            }

            var m = (int)Math.Log2(fftSize);
            FastFourierTransform.FFT(true, m, complex);

            var magnitudes = new float[fftSize / 2];
            for (int i = 0; i < magnitudes.Length; i++)
            {
                var re = complex[i].X;
                var im = complex[i].Y;
                magnitudes[i] = (float)Math.Sqrt((re * re) + (im * im));
            }

            return magnitudes;
        }

        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
                _writeIndex = 0;
            }
        }
    }
}
