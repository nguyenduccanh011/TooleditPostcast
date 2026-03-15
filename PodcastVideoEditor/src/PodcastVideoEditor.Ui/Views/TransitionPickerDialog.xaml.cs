#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Transition gallery dialog. User picks one of four preset transitions
    /// and optionally sets the duration before clicking Apply.
    /// </summary>
    public partial class TransitionPickerDialog : Window
    {
        private static readonly Color SelectedBorder = Color.FromRgb(0x42, 0xa5, 0xf5); // blue
        private static readonly Color DefaultBorder  = Color.FromRgb(0x37, 0x47, 0x4f); // dark

        /// <summary>The transition key selected by the user (e.g. "fade", "wipe").</summary>
        public string SelectedTransition { get; private set; } = "fade";

        /// <summary>Duration in seconds the user entered (default 0.5).</summary>
        public double SelectedDuration { get; private set; } = 0.5;

        /// <summary>
        /// Create and pre-select the transition that the segment already has.
        /// </summary>
        public TransitionPickerDialog(string currentTransition, double currentDuration)
        {
            InitializeComponent();
            DurationTextBox.Text = currentDuration.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            SetSelection(currentTransition);
        }

        // ── tile click ──────────────────────────────────────────────────────

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string tag)
                SetSelection(tag);
        }

        private void SetSelection(string transition)
        {
            SelectedTransition = transition;
            SelectedLabel.Text = transition;

            // Reset all tiles, then highlight the chosen one
            foreach (var tile in new[] { FadeTile, WipeTile, ZoomTile, DissolveTile })
                tile.BorderBrush = new SolidColorBrush(DefaultBorder);

            var chosen = transition switch
            {
                "wipe"    => WipeTile,
                "zoom"    => ZoomTile,
                "dissolve"=> DissolveTile,
                _         => FadeTile
            };
            chosen.BorderBrush = new SolidColorBrush(SelectedBorder);
        }

        // ── buttons ─────────────────────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(DurationTextBox.Text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0)
                SelectedDuration = d;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
