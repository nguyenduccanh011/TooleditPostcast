#nullable enable
namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// Root view model for MainWindow.
/// </summary>
public sealed class MainViewModel
{
    public MainViewModel(ProjectViewModel projectViewModel, RenderViewModel renderViewModel)
    {
        ProjectViewModel = projectViewModel;
        RenderViewModel = renderViewModel;
    }

    public ProjectViewModel ProjectViewModel { get; }
    public RenderViewModel RenderViewModel { get; }
}
