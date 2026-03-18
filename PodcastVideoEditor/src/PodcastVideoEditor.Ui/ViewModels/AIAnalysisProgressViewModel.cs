#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// ViewModel for the AI analysis progress dialog.
/// Receives <see cref="AIAnalysisProgressReport"/> updates from the orchestrator via
/// <see cref="Report"/> and exposes them for data binding.
/// </summary>
public partial class AIAnalysisProgressViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts;

    /// <summary>Fired when the window should be closed (operation finished or cancelled).</summary>
    public event Action? CloseRequested;

    public AIAnalysisProgressViewModel(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    [ObservableProperty]
    private string stepMessage = "Đang khởi động…";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool hasError;

    [ObservableProperty]
    private string errorDetail = string.Empty;

    /// <summary>Called by TimelineViewModel progress callback on each orchestrator step.</summary>
    public void Report(Core.Services.AI.AIAnalysisProgressReport report)
    {
        StepMessage     = report.Message;
        ProgressPercent = report.Percent;
        HasError        = report.IsError;
        ErrorDetail     = report.ErrorDetail ?? string.Empty;
    }

    /// <summary>
    /// Called by TimelineViewModel once the operation completes (success or failure).
    /// Closes the dialog.
    /// </summary>
    public void NotifyComplete() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Cancel()
    {
        StepMessage = "Đang huỷ…";
        try { _cts.Cancel(); } catch { /* already cancelled */ }
    }
}
