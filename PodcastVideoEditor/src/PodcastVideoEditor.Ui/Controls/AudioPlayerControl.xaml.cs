using Microsoft.Win32;
using PodcastVideoEditor.Ui.ViewModels;
using Serilog;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PodcastVideoEditor.Ui.Controls
{
    /// <summary>
    /// Audio Player Control - handles audio file selection and playback UI.
    /// </summary>
    public partial class AudioPlayerControl : UserControl
    {
        private bool _isDraggingSlider = false;
        private bool _ownsViewModel = false;

        public AudioPlayerControl()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Log.Information("AudioPlayerControl initialized");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureViewModel();
        }

        private AudioPlayerViewModel EnsureViewModel()
        {
            if (DataContext is AudioPlayerViewModel existing)
                return existing;

            var viewModel = new AudioPlayerViewModel();
            DataContext = viewModel;
            _ownsViewModel = true;
            return viewModel;
        }

        private void SelectAudioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Select Audio File",
                    Filter = GetAudioFileFilter(),
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var filePath = openFileDialog.FileName;
                    
                    // Copy to app cache directory
                    var appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PodcastVideoEditor", "AudioCache");
                    
                    Directory.CreateDirectory(appDataPath);
                    var cacheFilePath = Path.Combine(appDataPath, Path.GetFileName(filePath));
                    
                    File.Copy(filePath, cacheFilePath, overwrite: true);
                    
                    // Load audio
                    EnsureViewModel().LoadAudioCommand.Execute(cacheFilePath);
                    UpdateStatusText("Audio loaded successfully");
                    
                    Log.Information("Audio file selected and cached: {FileName}", Path.GetFileName(filePath));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error selecting audio file");
                UpdateStatusText($"Error: {ex.Message}");
                MessageBox.Show($"Error loading audio:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSlider && sender is Slider slider)
            {
                _isDraggingSlider = false;
                EnsureViewModel().SeekCommand.Execute(slider.Value);
            }
        }

        private string GetAudioFileFilter()
        {
            return "Audio Files (*.mp3;*.wav;*.m4a;*.flac;*.aac)|*.mp3;*.wav;*.m4a;*.flac;*.aac|All Files (*.*)|*.*";
        }

        private void UpdateStatusText(string message)
        {
            StatusText.Text = $"{message} ({DateTime.Now:HH:mm:ss})";
        }

        public void Cleanup()
        {
            if (_ownsViewModel && DataContext is AudioPlayerViewModel viewModel)
            {
                viewModel.Dispose();
            }
        }
    }
}
