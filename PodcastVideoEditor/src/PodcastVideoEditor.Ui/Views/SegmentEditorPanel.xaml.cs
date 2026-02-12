using Microsoft.Win32;
using PodcastVideoEditor.Ui.ViewModels;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Code-behind for SegmentEditorPanel.xaml
    /// Shows/hides properties based on segment selection.
    /// ST-9: Timeline Editor
    /// </summary>
    public partial class SegmentEditorPanel : UserControl
    {
        private TimelineViewModel? _viewModel;

        public SegmentEditorPanel()
        {
            InitializeComponent();
            Loaded += SegmentEditorPanel_Loaded;
        }

        private void SegmentEditorPanel_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as TimelineViewModel;

            if (_viewModel != null)
            {
                // Subscribe to selected segment changes
                _viewModel.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(TimelineViewModel.SelectedSegment))
                    {
                        UpdateVisibility();
                    }
                };

                // Initial state
                UpdateVisibility();

                Log.Information("SegmentEditorPanel loaded, ViewModel connected");
            }
        }

        /// <summary>
        /// Update visibility of properties based on selection state.
        /// </summary>
        private void UpdateVisibility()
        {
            if (_viewModel?.SelectedSegment == null)
            {
                NoSelectionText.Visibility = Visibility.Visible;
                PropertiesPanel.Visibility = Visibility.Collapsed;
                RefreshBackgroundInfo();
            }
            else
            {
                NoSelectionText.Visibility = Visibility.Collapsed;
                PropertiesPanel.Visibility = Visibility.Visible;
                RefreshBackgroundInfo();
            }
        }

        private void RefreshBackgroundInfo()
        {
            if (BackgroundPathText == null)
                return;

            if (_viewModel?.SelectedSegment == null)
            {
                BackgroundPathText.Text = "No asset selected";
                BackgroundPathText.ToolTip = null;
                return;
            }

            var assetId = _viewModel.SelectedSegment.BackgroundAssetId;
            var assets = _viewModel.ProjectViewModel.CurrentProject?.Assets;
            if (!string.IsNullOrWhiteSpace(assetId) && assets != null)
            {
                var asset = assets.FirstOrDefault(a => a.Id == assetId);
                if (asset != null)
                {
                    BackgroundPathText.Text = asset.FileName;
                    BackgroundPathText.ToolTip = asset.FilePath;
                    return;
                }

                BackgroundPathText.Text = "Asset not found";
                BackgroundPathText.ToolTip = null;
                return;
            }

            BackgroundPathText.Text = "No asset selected";
            BackgroundPathText.ToolTip = null;
        }

        private async void ChooseBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.SelectedSegment == null)
                return;

            if (!string.Equals(_viewModel.SelectedSegment.Kind, "visual", StringComparison.OrdinalIgnoreCase))
                return;

            var projectVm = _viewModel.ProjectViewModel;
            var project = projectVm.CurrentProject;
            if (project == null)
            {
                _viewModel.StatusMessage = "No project loaded";
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select image or video",
                Filter = "Images/Videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.mkv;*.avi;*.webm|All Files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var assetType = InferAssetType(dialog.FileName);
                var asset = await projectVm.AddAssetToCurrentProjectAsync(dialog.FileName, assetType);
                if (asset == null)
                    return;

                _viewModel.SelectedSegment.BackgroundAssetId = asset.Id;
                await projectVm.SaveProjectAsync();
                RefreshBackgroundInfo();
                _viewModel.StatusMessage = $"Background set: {asset.FileName}";
                Log.Information("Background asset assigned to segment {SegmentId}: {AssetId}", _viewModel.SelectedSegment.Id, asset.Id);
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = $"Error setting background: {ex.Message}";
                Log.Error(ex, "Error choosing background for segment {SegmentId}", _viewModel.SelectedSegment.Id);
            }
        }

        private async void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.SelectedSegment == null)
                return;

            _viewModel.SelectedSegment.BackgroundAssetId = null;
            await _viewModel.ProjectViewModel.SaveProjectAsync();
            RefreshBackgroundInfo();
            _viewModel.StatusMessage = "Background cleared";
            Log.Information("Background cleared for segment {SegmentId}", _viewModel.SelectedSegment.Id);
        }

        private static string InferAssetType(string filePath)
        {
            var extension = System.IO.Path.GetExtension(filePath).Trim('.').ToLowerInvariant();

            return extension switch
            {
                "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp" => "Image",
                "mp4" or "mov" or "mkv" or "avi" or "webm" => "Video",
                _ => "File"
            };
        }
    }
}
