#nullable enable
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Debounced autosave: schedules a save after a delay, resetting the timer on each new change.
/// Prevents rapid-fire saves while ensuring no change is lost.
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private readonly Func<Task> _saveAction;
    private readonly int _delayMs;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Create an autosave service.
    /// </summary>
    /// <param name="saveAction">Async action that performs the actual save.</param>
    /// <param name="delayMs">Debounce delay in milliseconds (default 2000ms).</param>
    public AutosaveService(Func<Task> saveAction, int delayMs = 2000)
    {
        _saveAction = saveAction ?? throw new ArgumentNullException(nameof(saveAction));
        _delayMs = delayMs;
    }

    /// <summary>
    /// Signal that a change occurred. The save will fire after the debounce delay
    /// unless another change arrives first (which resets the timer).
    /// </summary>
    public void RequestSave()
    {
        if (_disposed) return;

        lock (_lock)
        {
            // Cancel any pending debounce timer
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _ = RunDebouncedSaveAsync(token);
        }
    }

    private async Task RunDebouncedSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delayMs, token);
            if (token.IsCancellationRequested) return;
            await _saveAction();
            Log.Debug("Autosave completed");
        }
        catch (OperationCanceledException)
        {
            // Debounce reset — expected
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autosave failed");
        }
    }

    /// <summary>
    /// Force an immediate save (e.g. on app close), bypassing debounce.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        try
        {
            await _saveAction();
            Log.Debug("Autosave flush completed");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autosave flush failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
