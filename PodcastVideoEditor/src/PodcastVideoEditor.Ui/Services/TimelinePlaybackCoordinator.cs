using NAudio.Wave;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.Services;

internal interface IPlaybackDispatcher
{
    void Invoke(Action action);
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

internal sealed class WpfPlaybackDispatcher : IPlaybackDispatcher
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Send);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
                action();
        }, DispatcherPriority.Normal, cancellationToken).Task;
    }
}

internal sealed class TimelinePlaybackCoordinator : IDisposable
{
    private const int ActiveSyncIntervalMs = 33;
    private const int IdleSyncIntervalMs = 150;
    private const double PositionDeltaThreshold = 0.002;

    private readonly IAudioPlaybackService _audioService;
    private readonly IPlaybackDispatcher _dispatcher;
    private readonly Func<bool> _isScrubbing;
    private readonly Func<bool> _isLoopPlayback;
    private readonly Func<double> _getTotalDuration;
    private readonly Func<double> _getPlayheadPosition;
    private readonly Action<double> _setPlayheadPosition;
    private readonly Func<double> _getLastSyncedPlayhead;
    private readonly Action<double> _setLastSyncedPlayhead;
    private readonly Action<bool> _setIsPlaying;
    private readonly Action<double, bool> _updateSegmentAudioPlayback;
    private readonly CancellationTokenSource _syncCts = new();
    private readonly Task _syncLoopTask;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private Stopwatch? _wallClockPlayback;
    private double _wallClockBasePosition;
    private int _playbackInteractionVersion;
    private bool _disposed;

    public TimelinePlaybackCoordinator(
        IAudioPlaybackService audioService,
        IPlaybackDispatcher dispatcher,
        Func<bool> isScrubbing,
        Func<bool> isLoopPlayback,
        Func<double> getTotalDuration,
        Func<double> getPlayheadPosition,
        Action<double> setPlayheadPosition,
        Func<double> getLastSyncedPlayhead,
        Action<double> setLastSyncedPlayhead,
        Action<bool> setIsPlaying,
        Action<double, bool> updateSegmentAudioPlayback)
    {
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isScrubbing = isScrubbing ?? throw new ArgumentNullException(nameof(isScrubbing));
        _isLoopPlayback = isLoopPlayback ?? throw new ArgumentNullException(nameof(isLoopPlayback));
        _getTotalDuration = getTotalDuration ?? throw new ArgumentNullException(nameof(getTotalDuration));
        _getPlayheadPosition = getPlayheadPosition ?? throw new ArgumentNullException(nameof(getPlayheadPosition));
        _setPlayheadPosition = setPlayheadPosition ?? throw new ArgumentNullException(nameof(setPlayheadPosition));
        _getLastSyncedPlayhead = getLastSyncedPlayhead ?? throw new ArgumentNullException(nameof(getLastSyncedPlayhead));
        _setLastSyncedPlayhead = setLastSyncedPlayhead ?? throw new ArgumentNullException(nameof(setLastSyncedPlayhead));
        _setIsPlaying = setIsPlaying ?? throw new ArgumentNullException(nameof(setIsPlaying));
        _updateSegmentAudioPlayback = updateSegmentAudioPlayback ?? throw new ArgumentNullException(nameof(updateSegmentAudioPlayback));

        _audioService.PlaybackStarted += OnPlaybackStarted;
        _audioService.PlaybackStopped += OnPlaybackStopped;
        _syncLoopTask = Task.Run(() => RunSyncLoopAsync(_syncCts.Token));
    }

    public void NotifyUserInteraction() => Interlocked.Increment(ref _playbackInteractionVersion);

    public void ResetWallClockTracking()
    {
        _wallClockPlayback?.Stop();
        _wallClockPlayback = null;
    }

    private bool IsPlaybackTransitionCurrent(int version) => version == Volatile.Read(ref _playbackInteractionVersion);

    private void OnPlaybackStarted(object? sender, EventArgs e)
    {
        NotifyUserInteraction();
        // Wake the sync loop immediately so it switches from 150ms idle to 33ms active.
        try { _wakeSignal.Release(); } catch (SemaphoreFullException) { }
        // Do NOT push playhead position here. During pause the frozen position matches
        // what the user sees; pushing a live SampleAggregator read would show the
        // buffer-ahead value (~150-300ms forward on some hardware) for one frame,
        // causing a visible needle jump. The sync loop handles position + segment
        // audio updates within one frame (~33ms) of wake-up.
    }

    private void OnPlaybackStopped(object? sender, PlaybackStoppedEventArgs e)
    {
        NotifyUserInteraction();

        if (_audioService.PlaybackState != PlaybackState.Stopped)
        {
            Log.Debug("Coordinator.OnPlaybackStopped: state={State}, skipping reset", _audioService.PlaybackState);
            return;
        }

        Log.Information("Coordinator.OnPlaybackStopped: resetting playhead to 0 (state=Stopped)");
        _setLastSyncedPlayhead(0);
        _ = _dispatcher.InvokeAsync(() =>
        {
            _setPlayheadPosition(0);
            _setIsPlaying(false);
        }, _syncCts.Token);
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_audioService.IsPlaying || _isScrubbing())
                {
                    ResetWallClockTracking();
                    // Wait for wake signal OR timeout — whichever comes first.
                    // This replaces a flat 150ms delay: the signal fires immediately
                    // when Play() is called, so the loop switches to active sync
                    // within ~1ms instead of up to 150ms.
                    try { await _wakeSignal.WaitAsync(IdleSyncIntervalMs, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var totalDuration = _getTotalDuration();
                var audioDuration = _audioService.GetDuration();
                var currentPosition = _audioService.GetCurrentPosition();

                bool primaryAudioExhausted = audioDuration > 0 && currentPosition >= audioDuration - 0.05;
                if (primaryAudioExhausted && totalDuration > audioDuration)
                {
                    if (_wallClockPlayback == null || !_wallClockPlayback.IsRunning)
                    {
                        _wallClockBasePosition = Math.Max(currentPosition, _getPlayheadPosition());
                        _wallClockPlayback = Stopwatch.StartNew();
                    }

                    currentPosition = _wallClockBasePosition + _wallClockPlayback.Elapsed.TotalSeconds;
                }
                else
                {
                    ResetWallClockTracking();
                }

                if (totalDuration > 0 && currentPosition > totalDuration)
                    currentPosition = totalDuration;

                if (totalDuration > 0 && currentPosition >= totalDuration && _audioService.IsPlaying)
                {
                    int transitionVersion = Volatile.Read(ref _playbackInteractionVersion);

                    if (_isLoopPlayback())
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (!IsPlaybackTransitionCurrent(transitionVersion) || !_audioService.IsPlaying)
                                return;

                            _audioService.Seek(0);
                            _setPlayheadPosition(0);
                            _setLastSyncedPlayhead(0);
                            _updateSegmentAudioPlayback(0, true);
                        }, cancellationToken);
                    }
                    else
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (!IsPlaybackTransitionCurrent(transitionVersion) || !_audioService.IsPlaying)
                                return;

                            Log.Information("Coordinator: auto-pause at end (pos={Pos}s, total={Total}s, audioState={State})",
                                currentPosition, totalDuration, _audioService.PlaybackState);
                            _audioService.Pause();
                            _setIsPlaying(false);
                            _setPlayheadPosition(totalDuration);
                            _setLastSyncedPlayhead(totalDuration);
                        }, cancellationToken);
                    }

                    await Task.Delay(IdleSyncIntervalMs, cancellationToken);
                    continue;
                }

                if (Math.Abs(currentPosition - _getLastSyncedPlayhead()) < PositionDeltaThreshold)
                {
                    await Task.Delay(ActiveSyncIntervalMs, cancellationToken);
                    continue;
                }

                _setLastSyncedPlayhead(currentPosition);
                await _dispatcher.InvokeAsync(() =>
                {
                    _setPlayheadPosition(currentPosition);
                    _updateSegmentAudioPlayback(currentPosition, false);
                }, cancellationToken);

                await Task.Delay(ActiveSyncIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _audioService.PlaybackStarted -= OnPlaybackStarted;
        _audioService.PlaybackStopped -= OnPlaybackStopped;
        _syncCts.Cancel();
        ResetWallClockTracking();
        try { _wakeSignal.Release(); } catch (SemaphoreFullException) { } // unblock idle wait so loop can exit

        try
        {
            _syncLoopTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _syncCts.Dispose();
            _wakeSignal.Dispose();
        }
    }
}