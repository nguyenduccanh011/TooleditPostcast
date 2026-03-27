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
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
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

            // Prevent concurrent save executions (FlushAsync may also call _saveAction)
            await _saveSemaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return;
                await _saveAction();
                Log.Debug("Autosave completed");
            }
            finally
            {
                _saveSemaphore.Release();
            }

            // Clear the CTS so HasPendingSave returns false after a successful save
            lock (_lock)
            {
                if (_cts != null && _cts.Token == token)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
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
    /// True when a debounced save is pending (RequestSave was called but save hasn't fired yet).
    /// </summary>
    public bool HasPendingSave
    {
        get { lock (_lock) { return _cts != null; } }
    }

    /// <summary>
    /// Force an immediate save (e.g. on app close), bypassing debounce.
    /// Only runs the save action if there is a pending debounce timer;
    /// use <paramref name="force"/> to save unconditionally.
    /// </summary>
    public async Task FlushAsync(bool force = false)
    {
        if (_disposed) return;

        bool wasPending;
        lock (_lock)
        {
            wasPending = _cts != null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        if (!wasPending && !force) return;

        // Prevent concurrent save executions
        await _saveSemaphore.WaitAsync();
        try
        {
            await _saveAction();
            Log.Debug("Autosave flush completed");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autosave flush failed");
        }
        finally
        {
            _saveSemaphore.Release();
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
        _saveSemaphore.Dispose();
    }
}
