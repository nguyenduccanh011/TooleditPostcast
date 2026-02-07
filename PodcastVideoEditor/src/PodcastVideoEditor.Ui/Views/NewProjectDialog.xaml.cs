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
        public string AudioFilePath { get; private set; } = string.Empty;

        public NewProjectDialog()
        {
            InitializeComponent();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files (*.mp3;*.wav;*.m4a;*.flac;*.aac)|*.mp3;*.wav;*.m4a;*.flac;*.aac|All Files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                AudioFileTextBox.Text = openFileDialog.FileName;
                AudioFilePath = openFileDialog.FileName;
            }
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            ProjectName = ProjectNameTextBox.Text.Trim();
            AudioFilePath = AudioFileTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                MessageBox.Show("Please enter a project name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(AudioFilePath))
            {
                MessageBox.Show("Please select an audio file.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
