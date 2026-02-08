#nullable enable
namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// Root view model for MainWindow.
/// </summary>
public sealed class MainViewModel
{
    public MainViewModel(
        ProjectViewModel projectViewModel,
        RenderViewModel renderViewModel,
        CanvasViewModel canvasViewModel,
        AudioPlayerViewModel audioPlayerViewModel,
        VisualizerViewModel visualizerViewModel,
        TimelineViewModel timelineViewModel)
    {
        ProjectViewModel = projectViewModel;
        RenderViewModel = renderViewModel;
        CanvasViewModel = canvasViewModel;
        AudioPlayerViewModel = audioPlayerViewModel;
        VisualizerViewModel = visualizerViewModel;
        TimelineViewModel = timelineViewModel;
    }

    public ProjectViewModel ProjectViewModel { get; }
    public RenderViewModel RenderViewModel { get; }
    public CanvasViewModel CanvasViewModel { get; }
    public AudioPlayerViewModel AudioPlayerViewModel { get; }
    public VisualizerViewModel VisualizerViewModel { get; }
    public TimelineViewModel TimelineViewModel { get; }
}
