#nullable enable
using Microsoft.Win32;
using System.Windows;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Dialog for creating a new project.
    /// </summary>
    public partial class NewProjectDialog : Window
    {
        public string ProjectName { get; private set; } = string.Empty;

        public NewProjectDialog()
        {
            InitializeComponent();
        }

        // ...existing code...

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            ProjectName = ProjectNameTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                MessageBox.Show("Please enter a project name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
