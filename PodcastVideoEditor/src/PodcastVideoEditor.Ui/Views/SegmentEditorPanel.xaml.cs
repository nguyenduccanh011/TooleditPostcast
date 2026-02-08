using PodcastVideoEditor.Ui.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Serilog;

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
            }
            else
            {
                NoSelectionText.Visibility = Visibility.Collapsed;
                PropertiesPanel.Visibility = Visibility.Visible;
            }
        }
    }
}
