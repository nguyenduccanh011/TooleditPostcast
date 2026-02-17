#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for video rendering (MVVM Toolkit).
    /// </summary>
    public partial class RenderViewModel : ObservableObject
    {
        private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm"];

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
        private TimelineViewModel? _timelineViewModel;

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

        public void AttachTimeline(TimelineViewModel timelineViewModel)
        {
            _timelineViewModel = timelineViewModel;
        }

        /// <summary>
        /// Set resolution from menu (used by compact resolution button).
        /// </summary>
        [RelayCommand]
        public void SetResolution(string resolution)
        {
            if (!string.IsNullOrWhiteSpace(resolution) && ResolutionOptions.Contains(resolution))
                SelectedResolution = resolution;
        }

        /// <summary>
        /// Set quality from menu (used by compact quality button).
        /// </summary>
        [RelayCommand]
        public void SetQuality(string quality)
        {
            if (!string.IsNullOrWhiteSpace(quality) && QualityOptions.Contains(quality))
                SelectedQuality = quality;
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
                var timelineVisualSegments = BuildTimelineVisualSegments(project);
                var imagePath = string.Empty;

                // Fallback path for legacy single-image rendering when no timeline visual segments exist.
                if (timelineVisualSegments.Count == 0)
                    imagePath = await ResolveRenderImagePathAsync(project, _renderCancellationTokenSource.Token) ?? string.Empty;

                if (timelineVisualSegments.Count == 0 && string.IsNullOrWhiteSpace(imagePath))
                {
                    StatusMessage = "No visual segment/image available for render";
                    return;
                }

                var config = new RenderConfig
                {
                    AudioPath = project.AudioPath,
                    ImagePath = imagePath,
                    VisualSegments = timelineVisualSegments,
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

        private async Task<string?> ResolveRenderImagePathAsync(Project project, CancellationToken cancellationToken)
        {
            var segment = ResolvePreferredVisualSegment(project);
            if (segment == null || string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                return null;

            var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
            if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                return null;

            if (IsVideoAsset(asset))
            {
                var frameTime = ResolveFrameTimeWithinSegment(segment);
                return await FFmpegService.GetOrCreateVideoThumbnailPathAsync(asset.FilePath, frameTime, cancellationToken);
            }

            return asset.FilePath;
        }

        private List<RenderVisualSegment> BuildTimelineVisualSegments(Project project)
        {
            var track = project.Tracks?
                .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                .OrderBy(t => t.Order)
                .FirstOrDefault();

            if (track == null || track.Segments == null || track.Segments.Count == 0)
                return [];

            var segments = new List<RenderVisualSegment>();
            foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
            {
                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId) || segment.EndTime <= segment.StartTime)
                    continue;

                var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                    continue;

                segments.Add(new RenderVisualSegment
                {
                    SourcePath = asset.FilePath,
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    IsVideo = IsVideoAsset(asset),
                    SourceOffsetSeconds = 0
                });
            }

            return segments;
        }

        private Segment? ResolvePreferredVisualSegment(Project project)
        {
            if (_timelineViewModel?.SelectedSegment != null &&
                string.Equals(_timelineViewModel.SelectedSegment.Kind, "visual", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_timelineViewModel.SelectedSegment.BackgroundAssetId))
            {
                return _timelineViewModel.SelectedSegment;
            }

            var playhead = _timelineViewModel?.PlayheadPosition ?? 0;
            var visualTracks = project.Tracks?
                .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                .OrderBy(t => t.Order)
                .ToList();

            if (visualTracks == null || visualTracks.Count == 0)
                return null;

            foreach (var track in visualTracks)
            {
                var atPlayhead = track.Segments
                    .Where(s => !string.IsNullOrWhiteSpace(s.BackgroundAssetId))
                    .FirstOrDefault(s => playhead >= s.StartTime && playhead < s.EndTime);
                if (atPlayhead != null)
                    return atPlayhead;
            }

            return visualTracks
                .SelectMany(t => t.Segments)
                .Where(s => !string.IsNullOrWhiteSpace(s.BackgroundAssetId))
                .OrderBy(s => s.StartTime)
                .FirstOrDefault();
        }

        private double ResolveFrameTimeWithinSegment(Segment segment)
        {
            var playhead = _timelineViewModel?.PlayheadPosition ?? segment.StartTime;
            var offset = playhead - segment.StartTime;
            if (double.IsNaN(offset) || double.IsInfinity(offset))
                return 0;
            return Math.Max(0, offset);
        }

        private static bool IsVideoAsset(Asset asset)
        {
            if (string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                return true;

            var ext = System.IO.Path.GetExtension(asset.FilePath);
            return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }
    }
}
