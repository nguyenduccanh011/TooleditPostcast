using System;

namespace PodcastVideoEditor.Core.Models
{
    /// <summary>
    /// Configuration for visualizer rendering.
    /// </summary>
    public class VisualizerConfig
    {
        /// <summary>
        /// Number of frequency bands to display (32, 48, 64, or 128).
        /// </summary>
        public int BandCount { get; set; } = 32;

        /// <summary>
        /// Visual style of the visualizer.
        /// </summary>
        public VisualizerStyle Style { get; set; } = VisualizerStyle.Bars;

        /// <summary>
        /// Color palette to use.
        /// </summary>
        public ColorPalette ColorPalette { get; set; } = ColorPalette.Rainbow;

        /// <summary>
        /// Smoothing factor for bar decay (0.0 = no smoothing, 1.0 = max smoothing).
        /// Higher values make bars fall slower.
        /// </summary>
        public float SmoothingFactor { get; set; } = 0.6f;

        /// <summary>
        /// Minimum frequency to display (Hz).
        /// </summary>
        public int MinFrequency { get; set; } = 20;

        /// <summary>
        /// Maximum frequency to display (Hz).
        /// </summary>
        public int MaxFrequency { get; set; } = 20000;

        /// <summary>
        /// Bar width in pixels (for Bars style).
        /// </summary>
        public float BarWidth { get; set; } = 8f;

        /// <summary>
        /// Space between bars in pixels.
        /// </summary>
        public float BarSpacing { get; set; } = 2f;

        /// <summary>
        /// Minimum dB threshold to display (anything below is silent).
        /// </summary>
        public float MinDb { get; set; } = -80f;

        /// <summary>
        /// Maximum dB threshold to display (peak level).
        /// </summary>
        public float MaxDb { get; set; } = 0f;

        /// <summary>
        /// Whether to apply logarithmic frequency scaling.
        /// </summary>
        public bool UseLogarithmicScale { get; set; } = true;

        /// <summary>
        /// Peak hold time in milliseconds (how long peaks stay visible).
        /// </summary>
        public int PeakHoldTime { get; set; } = 300;

        /// <summary>
        /// Whether to show peak indicators.
        /// </summary>
        public bool ShowPeaks { get; set; } = true;

        /// <summary>
        /// Mirror mode: use only the lowest 60% of bands and display them symmetrically
        /// (left half = low→mid, right half = mid→low). Hides high-frequency bands that
        /// typically have little movement, and creates a visually appealing butterfly/symmetric look.
        /// </summary>
        public bool SymmetricMode { get; set; } = true;

        /// <summary>
        /// Custom primary color hex for Custom/Mono palette (#RRGGBB).
        /// </summary>
        public string PrimaryColorHex { get; set; } = "#00FF00";

        /// <summary>
        /// Comma-separated hex colors for Custom palette gradient.
        /// </summary>
        public string CustomGradientColors { get; set; } = "#FF0000,#00FF00,#0000FF";

        /// <summary>
        /// Darkness multiplier for bar gradient base (0 = no darkening, 1 = fully dark).
        /// </summary>
        public float BarGradientDarkness { get; set; } = 0.4f;

        /// <summary>
        /// Whether to enable gradient darkening on bar bases.
        /// </summary>
        public bool BarGradientEnabled { get; set; } = true;

        /// <summary>
        /// Override color for bar gradient base (#RRGGBB). Null/empty = use darkened version of bar color.
        /// </summary>
        public string? BarGradientBaseColorHex { get; set; }

        /// <summary>
        /// Validates configuration values.
        /// </summary>
        public bool Validate()
        {
            if (BandCount is not (32 or 48 or 64 or 128))
                return false;

            if (SmoothingFactor < 0f || SmoothingFactor > 1f)
                return false;

            if (MinFrequency < 1 || MaxFrequency > 48000)
                return false;

            if (MinFrequency >= MaxFrequency)
                return false;

            if (BarWidth <= 0 || BarSpacing < 0)
                return false;

            if (MinDb >= MaxDb)
                return false;

            if (PeakHoldTime < 0)
                return false;

            return true;
        }

        /// <summary>
        /// Creates a copy of this configuration.
        /// </summary>
        public VisualizerConfig Clone()
        {
            return new VisualizerConfig
            {
                BandCount = BandCount,
                Style = Style,
                ColorPalette = ColorPalette,
                SmoothingFactor = SmoothingFactor,
                MinFrequency = MinFrequency,
                MaxFrequency = MaxFrequency,
                BarWidth = BarWidth,
                BarSpacing = BarSpacing,
                MinDb = MinDb,
                MaxDb = MaxDb,
                UseLogarithmicScale = UseLogarithmicScale,
                PeakHoldTime = PeakHoldTime,
                ShowPeaks = ShowPeaks,
                SymmetricMode = SymmetricMode,
                PrimaryColorHex = PrimaryColorHex,
                CustomGradientColors = CustomGradientColors,
                BarGradientDarkness = BarGradientDarkness,
                BarGradientEnabled = BarGradientEnabled,
                BarGradientBaseColorHex = BarGradientBaseColorHex
            };
        }
    }
}
