using NAudio.Wave;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

public sealed class TimelinePlaybackCoordinatorTests
{
    [Fact]
    public async Task QueuedEofPause_IsIgnoredAfterUserSeeksBack()
    {
        var audio = new FakeAudioPlaybackService
        {
            CurrentPosition = 20,
            Duration = 20,
            PlaybackState = PlaybackState.Playing
        };
        var host = new FakeTimelineHost
        {
            TotalDuration = 20,
            PlayheadPosition = 20,
            LastSyncedPlayhead = 19.5,
            IsPlaying = true
        };
        var dispatcher = new ManualPlaybackDispatcher();

        using var sut = CreateCoordinator(audio, host, dispatcher);

        await WaitUntilAsync(() => dispatcher.PendingActions > 0);

        sut.NotifyUserInteraction();
        sut.ResetWallClockTracking();
        host.PlayheadPosition = 5;
        audio.CurrentPosition = 5;

        dispatcher.DrainPendingActions();
        await Task.Delay(100);

        Assert.Equal(0, audio.PauseCallCount);
        Assert.True(audio.IsPlaying);
        Assert.Equal(5, host.PlayheadPosition, 3);
    }

    [Fact]
    public async Task LoopPlayback_SeeksBackToStartAtEof()
    {
        var audio = new FakeAudioPlaybackService
        {
            CurrentPosition = 20,
            Duration = 20,
            PlaybackState = PlaybackState.Playing
        };
        var host = new FakeTimelineHost
        {
            TotalDuration = 20,
            PlayheadPosition = 20,
            LastSyncedPlayhead = 19.5,
            IsPlaying = true,
            IsLoopPlayback = true
        };

        using var sut = CreateCoordinator(audio, host, new ImmediatePlaybackDispatcher());

        await WaitUntilAsync(() => audio.SeekCalls.Contains(0));

        Assert.Contains(0, audio.SeekCalls);
        Assert.Equal(0, host.PlayheadPosition, 3);
        Assert.Equal(0, host.LastSyncedPlayhead, 3);
        Assert.True(audio.IsPlaying);
    }

    [Fact]
    public async Task ExplicitStopEvent_ResetsPlayheadToZero()
    {
        var audio = new FakeAudioPlaybackService
        {
            CurrentPosition = 12,
            Duration = 20,
            PlaybackState = PlaybackState.Stopped
        };
        var host = new FakeTimelineHost
        {
            TotalDuration = 20,
            PlayheadPosition = 12,
            LastSyncedPlayhead = 12,
            IsPlaying = true
        };

        using var sut = CreateCoordinator(audio, host, new ImmediatePlaybackDispatcher());

        audio.RaisePlaybackStopped();
        await WaitUntilAsync(() => !host.IsPlaying && Math.Abs(host.PlayheadPosition) < 0.001);

        Assert.False(host.IsPlaying);
        Assert.Equal(0, host.PlayheadPosition, 3);
        Assert.Equal(0, host.LastSyncedPlayhead, 3);
    }

    [Fact]
    public async Task PauseResume_PlayheadStaysAtPausePosition()
    {
        // Simulates: play at 10s → pause → resume. Playhead should NOT jump.
        var audio = new FakeAudioPlaybackService
        {
            CurrentPosition = 10.0,
            Duration = 60,
            PlaybackState = PlaybackState.Playing
        };
        var host = new FakeTimelineHost
        {
            TotalDuration = 60,
            PlayheadPosition = 10.0,
            LastSyncedPlayhead = 10.0,
            IsPlaying = true
        };

        using var sut = CreateCoordinator(audio, host, new ImmediatePlaybackDispatcher());

        // Let coordinator sync once
        await Task.Delay(50);

        // Pause: position stays at 10.0
        audio.Pause();
        host.IsPlaying = false;

        // Wait for coordinator to enter idle
        await Task.Delay(50);

        // Resume: position should still be ~10.0 (no jump)
        // Advance position slightly (simulates NAudio reading a few more samples after resume)
        audio.CurrentPosition = 10.05;
        audio.Play();
        host.IsPlaying = true;

        // Wait for coordinator sync loop to push an update after resume.
        // OnPlaybackStarted no longer pushes position (to prevent needle jump),
        // so we wait for the sync loop's natural 33ms cycle instead.
        await WaitUntilAsync(() => host.UpdateCalls.Any(c => Math.Abs(c.position - 10.05) < 0.5), timeoutMs: 2000);

        // Playhead should be near 10.0 — NOT some future position (e.g. 10.3+)
        Assert.InRange(host.PlayheadPosition, 9.5, 10.5);
        Assert.Empty(audio.SeekCalls); // No seek should have been triggered
    }

    [Fact]
    public async Task WakeSignal_CoordinatorActivatesImmediatelyOnResume()
    {
        var audio = new FakeAudioPlaybackService
        {
            CurrentPosition = 5.0,
            Duration = 60,
            PlaybackState = PlaybackState.Paused
        };
        var host = new FakeTimelineHost
        {
            TotalDuration = 60,
            PlayheadPosition = 5.0,
            LastSyncedPlayhead = 5.0,
            IsPlaying = false
        };

        using var sut = CreateCoordinator(audio, host, new ImmediatePlaybackDispatcher());

        // Coordinator is in idle mode. Start playback and measure activation time.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        audio.CurrentPosition = 5.1; // Advance slightly so delta > threshold
        audio.Play();
        host.IsPlaying = true;

        // Wait for the coordinator to push an update (should be near-instant with wake signal)
        await WaitUntilAsync(() =>
            host.UpdateCalls.Any(c => Math.Abs(c.position - 5.1) < 0.5), timeoutMs: 500);
        sw.Stop();

        // With wake signal, activation should be <100ms (was up to 150ms before)
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Coordinator took {sw.ElapsedMilliseconds}ms to activate after resume (expected <100ms)");
    }

    private static TimelinePlaybackCoordinator CreateCoordinator(
        FakeAudioPlaybackService audio,
        FakeTimelineHost host,
        IPlaybackDispatcher dispatcher)
    {
        return new TimelinePlaybackCoordinator(
            audio,
            dispatcher,
            () => host.IsScrubbing,
            () => host.IsLoopPlayback,
            () => host.TotalDuration,
            () => host.PlayheadPosition,
            value => host.PlayheadPosition = value,
            () => host.LastSyncedPlayhead,
            value => host.LastSyncedPlayhead = value,
            value => host.IsPlaying = value,
            (position, forceResync) => host.UpdateCalls.Add((position, forceResync)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for expected playback state change.");
    }

    private sealed class FakeTimelineHost
    {
        public bool IsScrubbing { get; set; }
        public bool IsLoopPlayback { get; set; }
        public bool IsPlaying { get; set; }
        public double TotalDuration { get; set; }
        public double PlayheadPosition { get; set; }
        public double LastSyncedPlayhead { get; set; }
        public List<(double position, bool forceResync)> UpdateCalls { get; } = new();
    }

    private sealed class FakeAudioPlaybackService : IAudioPlaybackService
    {
        public event EventHandler<PlaybackStoppedEventArgs>? PlaybackStopped;
        public event EventHandler<EventArgs>? PlaybackStarted;
        public event EventHandler<EventArgs>? PlaybackPaused;

        public double CurrentPosition { get; set; }
        public double Duration { get; set; }
        public PlaybackState PlaybackState { get; set; }
        public bool IsPlaying => PlaybackState == PlaybackState.Playing;
        public int PauseCallCount { get; private set; }
        public List<double> SeekCalls { get; } = new();

        public Task<AudioMetadata> LoadAudioAsync(string filePath)
        {
            return Task.FromResult(new AudioMetadata
            {
                FilePath = filePath,
                FileName = filePath,
                Duration = TimeSpan.FromSeconds(Duration),
                SampleRate = 44100,
                Channels = 2,
                CreatedAt = DateTime.UtcNow
            });
        }

        public void Play()
        {
            PlaybackState = PlaybackState.Playing;
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            PauseCallCount++;
            PlaybackState = PlaybackState.Paused;
            PlaybackPaused?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new PlaybackStoppedEventArgs());
        }

        public void Seek(double positionSeconds)
        {
            CurrentPosition = positionSeconds;
            SeekCalls.Add(positionSeconds);
        }

        public double GetCurrentPosition() => CurrentPosition;
        public double GetDuration() => Duration;
        public void SetVolume(float volume) { }
        public float GetVolume() => 1.0f;

        public Task PreDecodeAudioAsync(string filePath) => Task.CompletedTask;

        public void RaisePlaybackStopped() => PlaybackStopped?.Invoke(this, new PlaybackStoppedEventArgs());

        public void Dispose()
        {
        }
    }

    private sealed class ImmediatePlaybackDispatcher : IPlaybackDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                action();
            return Task.CompletedTask;
        }
    }

    private sealed class ManualPlaybackDispatcher : IPlaybackDispatcher
    {
        private readonly ConcurrentQueue<Action> _pendingActions = new();

        public int PendingActions => _pendingActions.Count;

        public void Invoke(Action action) => action();

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                _pendingActions.Enqueue(action);
            return Task.CompletedTask;
        }

        public void DrainPendingActions()
        {
            while (_pendingActions.TryDequeue(out var action))
                action();
        }
    }
}