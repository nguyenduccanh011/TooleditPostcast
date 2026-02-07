#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for video rendering (MVVM Toolkit).
    /// </summary>
    public partial class RenderViewModel : ObservableObject
    {
        [ObservableProperty]
        private string selectedResolution = "1080p";

        [ObservableProperty]
        private string selectedAspectRatio = "9:16";

        [ObservableProperty]
        private string selectedQuality = "Medium";

        [ObservableProperty]
        private int renderProgress;

        [ObservableProperty]
        private string statusMessage = "Ready to render";

        [ObservableProperty]
        private bool isRendering;

        [ObservableProperty]
        private bool canCancel;

        private CancellationTokenSource? _renderCancellationTokenSource;

        public ObservableCollection<string> ResolutionOptions { get; } = new()
        {
            "480p", "720p", "1080p"
        };

        public ObservableCollection<string> AspectRatioOptions { get; } = new()
        {
            "9:16", "16:9", "1:1", "4:5"
        };

        public ObservableCollection<string> QualityOptions { get; } = new()
        {
            "Low", "Medium", "High"
        };

        public RenderViewModel()
        {
        }

        /// <summary>
        /// Start render process.
        /// </summary>
        [RelayCommand]
        public async Task StartRenderAsync(Project? project)
        {
            if (project == null)
            {
                StatusMessage = "No project selected";
                return;
            }

            if (string.IsNullOrWhiteSpace(project.AudioPath))
            {
                StatusMessage = "Project has no audio file";
                return;
            }

            IsRendering = true;
            CanCancel = true;
            RenderProgress = 0;
            StatusMessage = "Initializing render...";

            _renderCancellationTokenSource = new CancellationTokenSource();

            try
            {
                // Build render config from selections
                var (width, height) = ParseResolution(SelectedResolution);
                var config = new RenderConfig
                {
                    AudioPath = project.AudioPath,
                    ImagePath = "C:\\Users\\DUC CANH PC\\Downloads\\31e91cdcded4508a09c5.jpg",
                    OutputPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PodcastVideoEditor",
                        "Renders",
                        $"{project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"),
                    ResolutionWidth = width,
                    ResolutionHeight = height,
                    AspectRatio = SelectedAspectRatio,
                    Quality = SelectedQuality,
                    FrameRate = 30
                };

                // Create placeholder image if it doesn't exist
                if (!System.IO.File.Exists(config.ImagePath))
                {
                    CreatePlaceholderImage(config.ImagePath);
                }

                var progress = new Progress<RenderProgress>(report =>
                {
                    RenderProgress = report.ProgressPercentage;
                    StatusMessage = report.Message;

                    if (report.IsComplete)
                    {
                        IsRendering = false;
                        CanCancel = false;
                        StatusMessage = $"Render completed: {System.IO.Path.GetFileName(config.OutputPath)}";
                        Log.Information("Render completed: {OutputPath}", config.OutputPath);
                    }
                });

                // Initialize FFmpeg if not already done
                if (!FFmpegService.IsInitialized())
                {
                    StatusMessage = "Detecting FFmpeg...";
                    var result = await FFmpegService.InitializeAsync();
                    
                    if (!result.IsValid)
                    {
                        StatusMessage = "FFmpeg not found. Please install FFmpeg.";
                        IsRendering = false;
                        CanCancel = false;
                        return;
                    }
                }

                // Start render
                var outputPath = await FFmpegService.RenderVideoAsync(config, progress, _renderCancellationTokenSource.Token);
                Log.Information("Render successful: {OutputPath}", outputPath);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Render cancelled by user";
                Log.Information("Render cancelled");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Render error: {ex.Message}";
                Log.Error(ex, "Render error");
            }
            finally
            {
                IsRendering = false;
                CanCancel = false;
                _renderCancellationTokenSource?.Dispose();
            }
        }

        /// <summary>
        /// Cancel render.
        /// </summary>
        [RelayCommand]
        public void CancelRender()
        {
            _renderCancellationTokenSource?.Cancel();
            FFmpegService.CancelRender();
            StatusMessage = "Cancelling render...";
        }

        /// <summary>
        /// Parse resolution string (e.g., "1080p" -> width=1080, height=1920 for 9:16).
        /// </summary>
        private (int width, int height) ParseResolution(string resolution)
        {
            var baseHeight = resolution switch
            {
                "480p" => 480,
                "720p" => 720,
                "1080p" => 1080,
                _ => 1080
            };

            // Calculate width based on aspect ratio
            var (ratioW, ratioH) = ParseAspectRatio(SelectedAspectRatio);
            var width = (int)(baseHeight * ratioW / ratioH);

            return (width, baseHeight);
        }

        /// <summary>
        /// Parse aspect ratio string (e.g., "9:16" -> 9, 16).
        /// </summary>
        private (int width, int height) ParseAspectRatio(string aspectRatio)
        {
            var parts = aspectRatio.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                return (w, h);
            }

            return (9, 16); // Default to 9:16
        }

        /// <summary>
        /// Create a placeholder image for testing.
        /// </summary>
        private void CreatePlaceholderImage(string imagePath)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(imagePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                // Create a simple solid color image (using System.Drawing as fallback)
                // For now, just copy a placeholder
                if (!System.IO.File.Exists(imagePath))
                {
                    Log.Warning("Placeholder image not found: {ImagePath}", imagePath);
                    // In production, generate or provide a default image
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating placeholder image");
            }
        }
    }
}
