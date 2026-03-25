using Microsoft.Win32;
using PodcastVideoEditor.Ui.ViewModels;
using Serilog;
using System;
using System.ComponentModel;
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
        private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

        public SegmentEditorPanel()
        {
            InitializeComponent();
            Loaded += SegmentEditorPanel_Loaded;
            Unloaded += SegmentEditorPanel_Unloaded;
        }

        private void SegmentEditorPanel_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as TimelineViewModel;

            if (_viewModel != null)
            {
                // Subscribe to selected segment changes
                _viewModelPropertyChangedHandler = (s, args) =>
                {
                    if (args.PropertyName == nameof(TimelineViewModel.SelectedSegment))
                    {
                        UpdateVisibility();
                    }
                };
                _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

                // Initial state
                UpdateVisibility();

                Log.Information("SegmentEditorPanel loaded, ViewModel connected");
            }
        }

        private void SegmentEditorPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null && _viewModelPropertyChangedHandler != null)
            {
                _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
                _viewModelPropertyChangedHandler = null;
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

            var dialog = new OpenFileDialog
            {
                Title = "Select image or video",
                Filter = "Images/Videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.mkv;*.avi;*.webm|All Files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            if (await _viewModel.SetSegmentBackgroundAsync(dialog.FileName))
                RefreshBackgroundInfo();
        }

        private async void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.SelectedSegment == null)
                return;

            await _viewModel.ClearSegmentBackgroundAsync();
            RefreshBackgroundInfo();
        }

    }
}
