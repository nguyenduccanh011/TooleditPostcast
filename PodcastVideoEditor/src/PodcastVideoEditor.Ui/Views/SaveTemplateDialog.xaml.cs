#nullable enable
using System.Windows;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Prompt for saving the current project layout as a reusable template.
/// </summary>
public partial class SaveTemplateDialog : Window
{
    public string TemplateName { get; private set; } = string.Empty;
    public string TemplateDescription { get; private set; } = string.Empty;

    public SaveTemplateDialog(string projectName)
    {
        InitializeComponent();
        TemplateNameTextBox.Text = string.IsNullOrWhiteSpace(projectName)
            ? "New Template"
            : $"{projectName} Template";
        TemplateNameTextBox.SelectAll();
        TemplateNameTextBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = TemplateNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = "Template name is required.";
            return;
        }

        TemplateName = name;
        TemplateDescription = DescriptionTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
