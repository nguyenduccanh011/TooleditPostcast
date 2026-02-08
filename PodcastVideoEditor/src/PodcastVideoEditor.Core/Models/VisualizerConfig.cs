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
        /// Validates configuration values.
        /// </summary>
        public bool Validate()
        {
            if (BandCount != 32 && BandCount != 48 && BandCount != 64 && BandCount != 128)
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
                ShowPeaks = ShowPeaks
            };
        }
    }
}
