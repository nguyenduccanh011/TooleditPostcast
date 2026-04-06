#nullable enable
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.Dialogs
{
    /// <summary>
    /// Dialog for selecting an image file. Provides file browser and preview.
    /// Supports: PNG, JPG, JPEG, WEBP, BMP, GIF, SVG (max 50 MB).
    /// </summary>
    public partial class SelectImageDialog : Window
    {
        private static string? _lastBrowsedFolder;
        
        private static readonly string[] SupportedExtensions = 
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".svg"
        };

        private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB

        public SelectImageDialog()
        {
            InitializeComponent();
            
            // Initialize from last browsed folder or default to Pictures
            string initialFolder = _lastBrowsedFolder ?? 
                                 Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            
            if (!Directory.Exists(initialFolder))
                initialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            BrowseFolder(initialFolder);
        }

        /// <summary>
        /// Get the selected file path. Returns null if canceled.
        /// </summary>
        public string? SelectedFilePath { get; private set; }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder containing images",
                SelectedPath = CurrentPathTextBlock.Text
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                BrowseFolder(dialog.SelectedPath);
            }
        }

        private void BrowseFolder(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    ErrorMessageTextBlock.Text = $"Folder not found: {folderPath}";
                    return;
                }

                CurrentPathTextBlock.Text = folderPath;
                    _lastBrowsedFolder = folderPath;
                FileListBox.Items.Clear();
                PreviewImage.Source = null;
                PreviewInfoTextBlock.Text = "No file selected";
                SelectedFileTextBlock.Text = "No file selected";
                ErrorMessageTextBlock.Text = "";

                // Get image files
                var imageFiles = Directory.GetFiles(folderPath)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                    .OrderBy(f => Path.GetFileName(f))
                    .ToList();

                foreach (var file in imageFiles)
                {
                    FileListBox.Items.Add(Path.GetFileName(file));
                }

                if (imageFiles.Count == 0)
                {
                    ErrorMessageTextBlock.Text = "No image files found in this folder";
                }
            }
            catch (Exception ex)
            {
                ErrorMessageTextBlock.Text = $"Error: {ex.Message}";
                Log.Error(ex, "Error browsing folder");
            }
        }

        private void FileListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FileListBox.SelectedItem is string fileName)
            {
                string filePath = Path.Combine(CurrentPathTextBlock.Text, fileName);
                try
                {
                    // Validate file
                    if (!File.Exists(filePath))
                    {
                        ErrorMessageTextBlock.Text = "File not found";
                        return;
                    }

                    var fileInfo = new FileInfo(filePath);
                    
                    if (fileInfo.Length > MaxFileSizeBytes)
                    {
                        ErrorMessageTextBlock.Text = $"File too large ({fileInfo.Length / (1024 * 1024)} MB > 50 MB)";
                        PreviewImage.Source = null;
                        PreviewInfoTextBlock.Text = "File too large";
                        SelectedFileTextBlock.Text = fileName;
                        return;
                    }

                    ErrorMessageTextBlock.Text = "";
                    SelectedFileTextBlock.Text = fileName;

                    // Try to load preview
                    if (Path.GetExtension(filePath).ToLower() != ".svg")
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(filePath);
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 180;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            PreviewImage.Source = bitmap;
                            PreviewInfoTextBlock.Text = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}\n{fileInfo.Length / 1024} KB";
                        }
                        catch (Exception ex)
                        {
                            PreviewImage.Source = null;
                            PreviewInfoTextBlock.Text = "Preview unavailable";
                            Log.Error(ex, "Error loading image preview");
                        }
                    }
                    else
                    {
                        PreviewImage.Source = null;
                        PreviewInfoTextBlock.Text = "SVG format\n(no preview)";
                    }
                }
                catch (Exception ex)
                {
                    ErrorMessageTextBlock.Text = $"Error: {ex.Message}";
                    Log.Error(ex, "Error loading image");
                }
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (FileListBox.SelectedItem is string fileName)
            {
                string filePath = Path.Combine(CurrentPathTextBlock.Text, fileName);
                
                if (!File.Exists(filePath))
                {
                    ErrorMessageTextBlock.Text = "File not found";
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    ErrorMessageTextBlock.Text = $"File too large ({fileInfo.Length / (1024 * 1024)} MB > 50 MB)";
                    return;
                }

                // Save folder preference

                SelectedFilePath = filePath;
                DialogResult = true;
                Close();
            }
            else
            {
                ErrorMessageTextBlock.Text = "Please select an image file";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedFilePath = null;
            DialogResult = false;
            Close();
        }
    }
}
