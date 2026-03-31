#nullable enable
using PodcastVideoEditor.Core.Services;
using System;
using System.ComponentModel;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// Root view model for MainWindow.
/// Manages wiring between ViewModels for unified playback control.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    private readonly AudioPlayerViewModel _audioPlayerViewModel;
    private readonly TimelineViewModel _timelineViewModel;
    private PropertyChangedEventHandler? _audioPlayerPropertyChangedHandler;
    private bool _disposed;

    public SelectionSyncService SelectionSyncService { get; }
    public UndoRedoService UndoRedoService { get; }

    public MainViewModel(
        ProjectViewModel projectViewModel,
        RenderViewModel renderViewModel,
        CanvasViewModel canvasViewModel,
        AudioPlayerViewModel audioPlayerViewModel,
        VisualizerViewModel visualizerViewModel,
        TimelineViewModel timelineViewModel,
        LibraryViewModel libraryViewModel)
    {
        ArgumentNullException.ThrowIfNull(canvasViewModel);
        ArgumentNullException.ThrowIfNull(audioPlayerViewModel);
        ArgumentNullException.ThrowIfNull(timelineViewModel);

        ProjectViewModel = projectViewModel ?? throw new ArgumentNullException(nameof(projectViewModel));
        RenderViewModel = renderViewModel ?? throw new ArgumentNullException(nameof(renderViewModel));
        CanvasViewModel = canvasViewModel;
        AudioPlayerViewModel = audioPlayerViewModel;
        VisualizerViewModel = visualizerViewModel ?? throw new ArgumentNullException(nameof(visualizerViewModel));
        TimelineViewModel = timelineViewModel;
        LibraryViewModel = libraryViewModel ?? throw new ArgumentNullException(nameof(libraryViewModel));

        _audioPlayerViewModel = audioPlayerViewModel;
        _timelineViewModel = timelineViewModel;

        // Wire ViewModels for unified playback control
        canvasViewModel.SetAudioPlayerViewModel(audioPlayerViewModel);

        // ✅ Wire SelectionSyncService for canvas↔timeline selection sync
        SelectionSyncService = new SelectionSyncService();
        canvasViewModel.SetSelectionSyncService(SelectionSyncService);
        timelineViewModel.SetSelectionSyncService(SelectionSyncService);

        // ✅ Wire UndoRedoService for Ctrl+Z/Y support across timeline and canvas
        UndoRedoService = new UndoRedoService();
        timelineViewModel.SetUndoRedoService(UndoRedoService);
        canvasViewModel.SetUndoRedoService(UndoRedoService);

        // Subscribe to playback state changes for UI sync (trackable event handler)
        _audioPlayerPropertyChangedHandler = OnAudioPlayerPropertyChanged;
        _audioPlayerViewModel.PropertyChanged += _audioPlayerPropertyChangedHandler;
    }

    /// <summary>
    /// Handle audio player property changes (IsPlaying state sync).
    /// </summary>
    private void OnAudioPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioPlayerViewModel.IsPlaying))
        {
            _timelineViewModel.IsPlaying = _audioPlayerViewModel.IsPlaying;
        }
    }

    public ProjectViewModel ProjectViewModel { get; }
    public RenderViewModel RenderViewModel { get; }
    public CanvasViewModel CanvasViewModel { get; }
    public AudioPlayerViewModel AudioPlayerViewModel { get; }
    public VisualizerViewModel VisualizerViewModel { get; }
    public TimelineViewModel TimelineViewModel { get; }
    public LibraryViewModel LibraryViewModel { get; }

    /// <summary>
    /// Clean up event subscriptions to prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            if (_audioPlayerPropertyChangedHandler != null)
            {
                _audioPlayerViewModel.PropertyChanged -= _audioPlayerPropertyChangedHandler;
            }

            CanvasViewModel?.Dispose();
            TimelineViewModel?.Dispose();
            ProjectViewModel?.Dispose();
            AudioPlayerViewModel?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
        finally
        {
            _disposed = true;
        }
    }
}
