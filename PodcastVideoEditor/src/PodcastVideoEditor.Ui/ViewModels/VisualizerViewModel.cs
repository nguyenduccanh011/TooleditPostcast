using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using SkiaSharp.Views.Desktop;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for visualizer control and settings.
    /// </summary>
    public partial class VisualizerViewModel : ObservableObject
    {
        private readonly AudioService _audioService;
        private readonly VisualizerService _visualizerService;
        private bool _isInitialized;

        [ObservableProperty]
        private VisualizerConfig currentConfig;

        [ObservableProperty]
        private VisualizerStyle selectedStyle = VisualizerStyle.Bars;

        [ObservableProperty]
        private ColorPalette selectedPalette = ColorPalette.Rainbow;

        [ObservableProperty]
        private int selectedBandCount = 64;

        [ObservableProperty]
        private float smoothingFactor = 0.7f;

        [ObservableProperty]
        private float currentBitmap;

        [ObservableProperty]
        private bool isVisualizerRunning;

        [ObservableProperty]
        private string statusMessage = "Ready";

        [ObservableProperty]
        private int visualizerWidth = 800;

        [ObservableProperty]
        private int visualizerHeight = 300;

        // Observable collections for UI dropdowns
        public ObservableCollection<VisualizerStyle> AvailableStyles { get; }
        public ObservableCollection<ColorPalette> AvailablePalettes { get; }
        public ObservableCollection<int> AvailableBandCounts { get; }

        public VisualizerViewModel(AudioService audioService)
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _visualizerService = new VisualizerService(audioService);
            CurrentConfig = _visualizerService.Config;

            // Initialize collections
            AvailableStyles = new ObservableCollection<VisualizerStyle>
            {
                VisualizerStyle.Bars,
                VisualizerStyle.Waveform,
                VisualizerStyle.Circular
            };

            AvailablePalettes = new ObservableCollection<ColorPalette>
            {
                ColorPalette.Rainbow,
                ColorPalette.Fire,
                ColorPalette.Ocean,
                ColorPalette.Mono,
                ColorPalette.Purple
            };

            AvailableBandCounts = new ObservableCollection<int>
            {
                32,
                64,
                128
            };

            // Wire up property changes to refresh visualizer config
            PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);

            Log.Information("VisualizerViewModel initialized");
        }

        /// <summary>
        /// Initialize visualizer (call after view is loaded with dimensions).
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            try
            {
                UpdateVisualizerConfig();
                _visualizerService.Start(VisualizerWidth, VisualizerHeight);
                IsVisualizerRunning = true;
                StatusMessage = "Visualizer ready";
                _isInitialized = true;

                Log.Information("VisualizerViewModel initialized ({Width}x{Height})",
                    VisualizerWidth, VisualizerHeight);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize visualizer");
                StatusMessage = "Initialization failed";
            }
        }

        /// <summary>
        /// Start/stop visualizer based on audio playback.
        /// </summary>
        public void UpdatePlaybackState()
        {
            if (!_isInitialized)
                return;

            if (_audioService.IsPlaying && !_visualizerService.IsRunning)
            {
                _visualizerService.Start(VisualizerWidth, VisualizerHeight);
                IsVisualizerRunning = true;
            }
            else if (!_audioService.IsPlaying && _visualizerService.IsRunning)
            {
                _visualizerService.Stop();
                IsVisualizerRunning = false;
            }
        }

        /// <summary>
        /// Update visualizer configuration with current settings.
        /// </summary>
        private void UpdateVisualizerConfig()
        {
            try
            {
                var newConfig = new VisualizerConfig
                {
                    Style = SelectedStyle,
                    ColorPalette = SelectedPalette,
                    BandCount = SelectedBandCount,
                    SmoothingFactor = Math.Clamp(SmoothingFactor, 0f, 1f)
                };

                if (!newConfig.Validate())
                {
                    Log.Warning("Invalid visualizer config, using current");
                    return;
                }

                _visualizerService.SetConfig(newConfig);
                CurrentConfig = newConfig.Clone();
                StatusMessage = $"Config updated: {SelectedStyle}, {SelectedPalette}, {SelectedBandCount} bands";

                Log.Debug("Visualizer config updated: {Style}, {Palette}, {Bands} bands",
                    SelectedStyle, SelectedPalette, SelectedBandCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to update visualizer config");
                StatusMessage = "Config update failed";
            }
        }

        /// <summary>
        /// Handle property changes to refresh visualizer.
        /// </summary>
        private void OnPropertyChanged(string? propertyName)
        {
            if (!_isInitialized)
                return;

            switch (propertyName)
            {
                case nameof(SelectedStyle):
                case nameof(SelectedPalette):
                case nameof(SelectedBandCount):
                case nameof(SmoothingFactor):
                    UpdateVisualizerConfig();
                    break;

                case nameof(VisualizerWidth):
                case nameof(VisualizerHeight):
                    if (_visualizerService.IsRunning)
                    {
                        _visualizerService.Stop();
                        _visualizerService.Start(VisualizerWidth, VisualizerHeight);
                    }
                    break;
            }
        }

        /// <summary>
        /// Change visualization style.
        /// </summary>
        [RelayCommand]
        public void SetStyle(VisualizerStyle style)
        {
            SelectedStyle = style;
            Log.Information("Visualizer style changed to {Style}", style);
        }

        /// <summary>
        /// Change color palette.
        /// </summary>
        [RelayCommand]
        public void SetPalette(ColorPalette palette)
        {
            SelectedPalette = palette;
            Log.Information("Visualizer palette changed to {Palette}", palette);
        }

        /// <summary>
        /// Change band count.
        /// </summary>
        [RelayCommand]
        public void SetBandCount(int bandCount)
        {
            if (bandCount != 32 && bandCount != 64 && bandCount != 128)
                return;

            SelectedBandCount = bandCount;
            Log.Information("Visualizer band count changed to {BandCount}", bandCount);
        }

        /// <summary>
        /// Get current bitmap for display.
        /// </summary>
        public SkiaSharp.SKBitmap? GetCurrentFrame()
        {
            return _visualizerService.GetCurrentBitmap();
        }

        public void Dispose()
        {
            _visualizerService?.Dispose();
            Log.Information("VisualizerViewModel disposed");
        }
    }
}
