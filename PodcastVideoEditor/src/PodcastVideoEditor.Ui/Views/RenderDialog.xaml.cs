#nullable enable
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Modal dialog for rendering video. Opened from the title bar Render button.
/// </summary>
public partial class RenderDialog : Window
{
    /// <summary>The current project reference, used as CommandParameter for StartRenderCommand.</summary>
    public static readonly DependencyProperty ProjectProperty =
        DependencyProperty.Register(nameof(Project), typeof(Core.Models.Project), typeof(RenderDialog));

    public Core.Models.Project? Project
    {
        get => (Core.Models.Project?)GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public RenderDialog()
    {
        InitializeComponent();
    }

    /// <summary>Generic handler: opens a button's ContextMenu as a dropdown.</summary>
    private void DropdownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
