using NAudio.Dsp;
using NAudio.Wave;
using Serilog;
using System;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Captures samples from playback and provides FFT data for visualization.
    /// </summary>
    public sealed class SampleAggregator : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _ringBuffer;
        private readonly object _lock = new object();
        private int _writeIndex;

        public SampleAggregator(ISampleProvider source, int ringBufferSize = 8192)
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
                    // Mix down to mono for visualization
                    for (int n = 0; n < read; n++)
                    {
                        var sample = buffer[offset + n];
                        _ringBuffer[_writeIndex] = sample;
                        _writeIndex = (_writeIndex + 1) % _ringBuffer.Length;
                    }
                }
            }

            return read;
        }

        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
                _writeIndex = 0;
            }
        }

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
                    // Apply Hann window
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
                var mag = (float)Math.Sqrt((re * re) + (im * im));
                magnitudes[i] = mag;
            }

            return magnitudes;
        }
    }
}
