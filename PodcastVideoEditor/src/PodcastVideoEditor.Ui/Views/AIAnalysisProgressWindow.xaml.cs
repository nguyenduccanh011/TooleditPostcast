using PodcastVideoEditor.Ui.ViewModels;
using System.Windows;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Code-behind for the AI analysis progress dialog.
/// Shows step-by-step progress and a cancel button.
/// </summary>
public partial class AIAnalysisProgressWindow : Window
{
    public AIAnalysisProgressWindow(AIAnalysisProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += () => Dispatcher.Invoke(Close);
    }
}
