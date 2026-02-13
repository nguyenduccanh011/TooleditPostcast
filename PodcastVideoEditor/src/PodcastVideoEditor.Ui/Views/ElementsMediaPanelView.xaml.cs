using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Left column of CapCut-like editor: Elements/Media panel with Script and project assets.
/// DataContext should be MainViewModel (ProjectViewModel, TimelineViewModel).
/// </summary>
public partial class ElementsMediaPanelView : UserControl
{
    public ElementsMediaPanelView()
    {
        InitializeComponent();
    }
}
