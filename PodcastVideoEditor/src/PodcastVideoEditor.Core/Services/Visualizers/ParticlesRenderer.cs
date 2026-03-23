using PodcastVideoEditor.Core.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace PodcastVideoEditor.Core.Services.Visualizers;

/// <summary>
/// Particle system driven by spectrum amplitude.
/// Frequency bands control particle emission rate and velocity.
/// Particles rise upward, influenced by amplitude.
/// </summary>
public sealed class ParticlesRenderer : IVisualizerRenderer
{
    public string Name => "Particles";
    public string Description => "Spectrum-driven particle system with rising particles";
    public VisualizerStyle Style => VisualizerStyle.Particles;

    private const int MaxParticles = 400;
    private readonly List<Particle> _particles = new(MaxParticles);
    private readonly Random _rng = new();
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private long _lastTick;

    private struct Particle
    {
        public float X, Y;
        public float Vx, Vy;
        public float Life; // 0..1, decreases over time
        public float Size;
        public SKColor Color;
    }

    public void Render(SKCanvas canvas, float[] spectrum, float[] peakBars,
                       VisualizerConfig config, int width, int height, double colorTick)
    {
        var now = Environment.TickCount64;
        var dt = _lastTick == 0 ? 0.033f : Math.Min((now - _lastTick) / 1000f, 0.1f);
        _lastTick = now;

        var bandCount = Math.Min(config.BandCount, spectrum.Length);

        // Emit new particles based on spectrum amplitude
        for (int i = 0; i < bandCount; i++)
        {
            var value = Math.Clamp(spectrum[i], 0f, 1f);
            if (value < 0.1f) continue;

            // Emit probability proportional to amplitude
            if (_rng.NextDouble() > value * 0.6) continue;
            if (_particles.Count >= MaxParticles) break;

            var bandX = (i + 0.5f) / bandCount * width;
            var color = VisualizerColorHelper.GetNeonColor(i, bandCount, colorTick, config.ColorPalette);

            _particles.Add(new Particle
            {
                X = bandX + (_rng.NextSingle() - 0.5f) * (width / bandCount),
                Y = height * 0.5f + (_rng.NextSingle() - 0.5f) * height * 0.1f,
                Vx = (_rng.NextSingle() - 0.5f) * 30f,
                Vy = -value * 120f - 20f, // rise upward
                Life = 1f,
                Size = 2f + value * 4f,
                Color = color
            });
        }

        // Update and draw particles
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vy += 10f * dt; // slight gravity
            p.Life -= dt * 1.2f;

            if (p.Life <= 0 || p.Y < -10 || p.Y > height + 10)
            {
                _particles.RemoveAt(i);
                continue;
            }

            _particles[i] = p;

            var alpha = (byte)(Math.Clamp(p.Life, 0f, 1f) * 220);
            _paint.Color = p.Color.WithAlpha(alpha);
            canvas.DrawCircle(p.X, p.Y, p.Size * p.Life, _paint);
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
        _particles.Clear();
    }
}
