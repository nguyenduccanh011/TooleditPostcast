#nullable enable
using PodcastVideoEditor.Core.Models;
using System.Windows;

namespace PodcastVideoEditor.Ui.Views;

public partial class CapCutExportDialog : Window
{
    public static readonly DependencyProperty ProjectProperty =
        DependencyProperty.Register(nameof(Project), typeof(Project), typeof(CapCutExportDialog));

    public Project? Project
    {
        get => (Project?)GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public CapCutExportDialog()
    {
        InitializeComponent();
    }
}
