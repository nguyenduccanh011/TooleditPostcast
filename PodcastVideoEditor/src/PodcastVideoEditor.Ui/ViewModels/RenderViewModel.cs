#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
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

        [ObservableProperty]
        private string selectedResolution = "1080p";

        [ObservableProperty]
        private string selectedAspectRatio = "9:16";

        [ObservableProperty]
        private string selectedQuality = "Medium";

        [ObservableProperty]
        private string selectedScaleMode = "Fill";

        [ObservableProperty]
        private int frameRate = 30;

        [ObservableProperty]
        private string videoCodec = "h264";

        [ObservableProperty]
        private string audioCodec = "aac";

        /// <summary>Linear volume multiplier for the primary audio (original recording). Default 1.0 = unchanged.</summary>
        [ObservableProperty]
        private double primaryAudioVolume = 1.0;

        [ObservableProperty]
        private int renderProgress;

        [ObservableProperty]
        private string statusMessage = "Ready to render";

        [ObservableProperty]
        private bool isRendering;

        [ObservableProperty]
        private bool canCancel;

        /// <summary>True when the last render ended with an error/cancellation (shows notification in toolbar).</summary>
        [ObservableProperty]
        private bool hasError;

        [ObservableProperty]
        private string outputFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PodcastVideoEditor",
            "Renders");

        private CancellationTokenSource? _renderCancellationTokenSource;
        private TimelineViewModel? _timelineViewModel;
        private CanvasViewModel? _canvasViewModel;

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

        public ObservableCollection<string> ScaleModeOptions { get; } = new()
        {
            "Fill", "Fit", "Stretch"
        };

        public RenderViewModel()
        {
        }

        public void AttachTimeline(TimelineViewModel timelineViewModel)
        {
            _timelineViewModel = timelineViewModel;
        }

        public void AttachCanvas(CanvasViewModel canvasViewModel)
        {
            _canvasViewModel = canvasViewModel;
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

        [RelayCommand]
        public void SetScaleMode(string scaleMode)
        {
            if (!string.IsNullOrWhiteSpace(scaleMode) && ScaleModeOptions.Contains(scaleMode))
                SelectedScaleMode = scaleMode;
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

            IsRendering = true;
            CanCancel = true;
            HasError = false;
            RenderProgress = 0;
            StatusMessage = "Initializing render...";

            _renderCancellationTokenSource = new CancellationTokenSource();

            try
            {
                // Ensure output folder exists
                if (!System.IO.Directory.Exists(OutputFolder))
                    System.IO.Directory.CreateDirectory(OutputFolder);

                // Initialize FFmpeg early — BuildVisualizerSegmentsAsync needs GetFFmpegPath()
                // to be non-null for the offline bake subprocess.
                if (!FFmpegService.IsInitialized())
                {
                    StatusMessage = "Detecting FFmpeg...";
                    var initResult = await FFmpegService.InitializeAsync();

                    if (!initResult.IsValid)
                    {
                        StatusMessage = "FFmpeg not found. Please install FFmpeg.";
                        IsRendering = false;
                        CanCancel = false;
                        return;
                    }
                }

                // Create an immutable render snapshot — isolates the entire render pipeline
                // from live UI state (canvas, timeline). All downstream methods read from
                // the snapshot, never from _canvasViewModel or _timelineViewModel.
                var snapshot = CreateRenderSnapshot(project);

                // Build render config from selections
                var (width, height) = ParseResolution(SelectedResolution);

                // Compute a unified z-order map so all track types participate in one
                // global layering based on Track.Order (background → foreground).
                var zOrderMap = RenderSegmentBuilder.ComputeTrackZOrderMap(snapshot.Project);

                var timelineVisualSegments = RenderSegmentBuilder.BuildTimelineVisualSegments(snapshot.Project, width, height, snapshot.Elements, snapshot.CanvasWidth, snapshot.CanvasHeight, zOrderMap);
                var rasterizedTextVisuals  = RenderSegmentBuilder.BuildRasterizedTextSegments(snapshot.Project, width, height, snapshot.Elements, snapshot.CanvasWidth, snapshot.CanvasHeight, zOrderMap);
                var timelineAudioSegments  = RenderSegmentBuilder.BuildTimelineAudioSegments(snapshot.Project);

                // Merge all visual layers into one list — FFmpegCommandComposer sorts by ZOrder.
                timelineVisualSegments.AddRange(rasterizedTextVisuals);

                // Bake visualizer spectrum overlays and merge them
                StatusMessage = "Baking visualizer spectrum…";
                var hasVisualizerElements = snapshot.Elements?.OfType<VisualizerElement>().Any() == true;
                var visualizerSegments = await RenderSegmentBuilder.BuildVisualizerSegmentsAsync(
                    snapshot.Project, width, height, snapshot.Elements, snapshot.CanvasWidth, snapshot.CanvasHeight, FrameRate, _renderCancellationTokenSource.Token, zOrderMap);
                timelineVisualSegments.AddRange(visualizerSegments);

                // Warn when the canvas has visualizer elements but none were baked into render.
                // Common causes: missing audio file, FFmpeg error, invalid time range.
                if (hasVisualizerElements && visualizerSegments.Count == 0)
                {
                    var audioMissing = string.IsNullOrWhiteSpace(snapshot.Project.AudioPath)
                                       || !System.IO.File.Exists(snapshot.Project.AudioPath);
                    var reason = audioMissing
                        ? "audio file not found at project.AudioPath"
                        : "baking failed — check application logs for FFmpeg error details";
                    Log.Warning("Render: {Count} VisualizerElement(s) on canvas were NOT rendered — {Reason}",
                        snapshot.Elements!.OfType<VisualizerElement>().Count(), reason);
                    StatusMessage = $"⚠ Visualizer skipped: {reason}";
                    await Task.Delay(1500, _renderCancellationTokenSource.Token); // briefly show the warning
                    StatusMessage = "Continuing render without visualizer…";
                }
                var imagePath = string.Empty;

                // Fallback path for legacy single-image rendering when no timeline segments exist at all.
                if (timelineVisualSegments.Count == 0 && timelineAudioSegments.Count == 0)
                    imagePath = await ResolveRenderImagePathAsync(snapshot.Project, _renderCancellationTokenSource.Token) ?? string.Empty;

                if (timelineVisualSegments.Count == 0
                    && timelineAudioSegments.Count == 0 && string.IsNullOrWhiteSpace(imagePath))
                {
                    StatusMessage = "No visual segment/image available for render";
                    return;
                }

                // Warn when visual tracks have segments but all were skipped (missing asset files).
                if (timelineVisualSegments.Count == 0)
                {
                    bool hasVisualSegments = snapshot.Project.Tracks?
                        .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                        .Any(t => t.Segments?.Any(s => !string.IsNullOrWhiteSpace(s.BackgroundAssetId)) == true) == true;
                    if (hasVisualSegments)
                        Log.Warning("Render: visual segments detected but all skipped — asset files may be missing or inaccessible");
                }

                var config = new RenderConfig
                {
                    ImagePath = imagePath,
                    VisualSegments = timelineVisualSegments,
                    TextSegments   = [],  // Text is rasterized to PNG overlays (WYSIWYG)
                    AudioSegments  = timelineAudioSegments,
                    OutputPath = System.IO.Path.Combine(
                        OutputFolder,
                        $"{project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"),
                    ResolutionWidth = width,
                    ResolutionHeight = height,
                    AspectRatio = SelectedAspectRatio,
                    Quality = SelectedQuality,
                    FrameRate = FrameRate,
                    VideoCodec = VideoCodec,
                    AudioCodec = AudioCodec,
                    ScaleMode = SelectedScaleMode
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

                // Start render
                var outputPath = await FFmpegService.RenderVideoAsync(config, progress, _renderCancellationTokenSource.Token);
                Log.Information("Render successful: {OutputPath}", outputPath);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Render cancelled by user";
                HasError = true;
                Log.Information("Render cancelled");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Render error: {ex.Message}";
                HasError = true;
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
        /// Browse for output folder using FolderBrowserDialog.
        /// </summary>
        [RelayCommand]
        public void BrowseOutputFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Output Folder",
                InitialDirectory = System.IO.Directory.Exists(OutputFolder) ? OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                OutputFolder = dialog.FolderName;
        }

        /// <summary>
        /// Parse resolution string (e.g., "1080p" -> width=1080, height=1920 for 9:16).
        /// </summary>
        private (int width, int height) ParseResolution(string resolution)
        {
            return RenderSizing.ResolveRenderSize(resolution, SelectedAspectRatio);
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

            if (RenderSegmentBuilder.IsVideoAsset(asset))
            {
                var frameTime = ResolveFrameTimeWithinSegment(segment);
                return await FFmpegService.GetOrCreateVideoThumbnailPathAsync(asset.FilePath, frameTime, cancellationToken);
            }

            return asset.FilePath;
        }

        /// <summary>
        /// Build a transient project copy that merges live (unsaved) timeline track state
        /// so render always reflects what the user currently sees in the canvas/timeline.
        /// Deep-clones tracks and segments to avoid concurrency issues during render.
        /// </summary>
        private Project BuildLiveProject(Project project)
        {
            if (_timelineViewModel == null)
                return project;

            // Deep clone tracks + segments so render is isolated from concurrent edits
            var clonedTracks = _timelineViewModel.Tracks.Select(t =>
            {
                var clone = t.ShallowClone();
                clone.Segments = t.Segments?.Select(s => s.ShallowClone()).ToList()
                                 ?? new System.Collections.Generic.List<Segment>();
                return clone;
            }).ToList();

            var live = new Project
            {
                Id             = project.Id,
                Name           = project.Name,
                AudioPath      = project.AudioPath,
                Assets         = project.Assets,
                RenderSettings = project.RenderSettings,
                Tracks         = clonedTracks
            };
            return live;
        }

        /// <summary>
        /// Create render-isolated snapshot of canvas elements.
        /// Deep-clones all elements with IsVisible=true so preview visibility
        /// state does not bleed into the render pipeline.
        /// </summary>
        private IReadOnlyList<CanvasElement>? SnapshotElementsForRender()
        {
            var elements = _canvasViewModel?.Elements;
            if (elements == null || elements.Count == 0)
                return null;

            return elements.Select(e => e.CloneForRender()).ToList();
        }

        /// <summary>
        /// Create a complete immutable snapshot of all data needed for render.
        /// After this call, the render pipeline never reads from _canvasViewModel
        /// or _timelineViewModel — full isolation from live UI state.
        /// </summary>
        private RenderSnapshot CreateRenderSnapshot(Project project)
        {
            var liveProject = BuildLiveProject(project);
            var elements = SnapshotElementsForRender();
            var registry = ElementSegmentRegistry.Build(elements, liveProject.Tracks);

            return new RenderSnapshot
            {
                Project = liveProject,
                Elements = elements,
                CanvasWidth = (_canvasViewModel?.CanvasWidth is > 0 ? _canvasViewModel.CanvasWidth : 1920),
                CanvasHeight = (_canvasViewModel?.CanvasHeight is > 0 ? _canvasViewModel.CanvasHeight : 1080),
                Registry = registry
            };
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

        public void ApplyProjectRenderSettings(RenderSettings? settings)
        {
            if (settings == null)
                return;

            SelectedAspectRatio = RenderSizing.NormalizeAspectRatio(settings.AspectRatio);
            SelectedResolution = RenderSizing.InferResolutionLabel(settings.ResolutionWidth, settings.ResolutionHeight);
            SelectedQuality = QualityOptions.Contains(settings.Quality) ? settings.Quality : "Medium";
            SelectedScaleMode = "Fill";
            FrameRate = settings.FrameRate > 0 ? settings.FrameRate : 30;
            VideoCodec = string.IsNullOrWhiteSpace(settings.VideoCodec) ? "h264" : settings.VideoCodec;
            AudioCodec = string.IsNullOrWhiteSpace(settings.AudioCodec) ? "aac" : settings.AudioCodec;
        }

        public RenderSettings BuildProjectRenderSettings()
        {
            var (width, height) = ParseResolution(SelectedResolution);
            return new RenderSettings
            {
                ResolutionWidth = width,
                ResolutionHeight = height,
                AspectRatio = RenderSizing.NormalizeAspectRatio(SelectedAspectRatio),
                Quality = SelectedQuality,
                FrameRate = FrameRate,
                VideoCodec = VideoCodec,
                AudioCodec = AudioCodec
            };
        }
    }
}
