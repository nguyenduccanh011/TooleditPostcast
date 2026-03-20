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
        private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm"];

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

                // Use live timeline tracks (reflects unsaved changes the user made)
                var liveProject = BuildLiveProject(project);

                // Build render config from selections
                var (width, height) = ParseResolution(SelectedResolution);
                var timelineVisualSegments = BuildTimelineVisualSegments(liveProject, width, height);
                var rasterizedTextVisuals  = BuildRasterizedTextSegments(liveProject, width, height);
                var timelineAudioSegments  = BuildTimelineAudioSegments(liveProject);

                // Merge rasterized text images into visual segments (rendered on top)
                timelineVisualSegments.AddRange(rasterizedTextVisuals);
                var imagePath = string.Empty;

                // Fallback path for legacy single-image rendering when no timeline segments exist at all.
                if (timelineVisualSegments.Count == 0 && timelineAudioSegments.Count == 0)
                    imagePath = await ResolveRenderImagePathAsync(liveProject, _renderCancellationTokenSource.Token) ?? string.Empty;

                if (timelineVisualSegments.Count == 0
                    && timelineAudioSegments.Count == 0 && string.IsNullOrWhiteSpace(imagePath))
                {
                    StatusMessage = "No visual segment/image available for render";
                    return;
                }

                // Warn when visual tracks have segments but all were skipped (missing asset files).
                if (timelineVisualSegments.Count == 0)
                {
                    bool hasVisualSegments = liveProject.Tracks?
                        .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                        .Any(t => t.Segments?.Any(s => !string.IsNullOrWhiteSpace(s.BackgroundAssetId)) == true) == true;
                    if (hasVisualSegments)
                        Log.Warning("Render: visual segments detected but all skipped — asset files may be missing or inaccessible");
                }

                var config = new RenderConfig
                {
                    AudioPath = project.AudioPath,
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

            if (IsVideoAsset(asset))
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
        /// Collect visual (image/video) segments from ALL visible visual tracks, ordered back→front.
        /// When a linked canvas element exists for a segment, its position and size are mapped to
        /// render coordinates so the output matches the canvas preview exactly.
        /// </summary>
        private List<RenderVisualSegment> BuildTimelineVisualSegments(Project project, int renderWidth, int renderHeight)
        {
            var visualTracks = project.Tracks?
                .Where(t => string.Equals(t.TrackType, "visual", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                .OrderBy(t => t.Order)
                .ToList();

            if (visualTracks == null || visualTracks.Count == 0)
                return [];

            var canvasWidth  = _canvasViewModel?.CanvasWidth  ?? 0;
            var canvasHeight = _canvasViewModel?.CanvasHeight ?? 0;
            var elements     = _canvasViewModel?.Elements;

            var segments = new List<RenderVisualSegment>();
            foreach (var track in visualTracks)
            {
                if (track.Segments == null) continue;
                foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
                {
                    if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId) || segment.EndTime <= segment.StartTime)
                        continue;

                    var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                    if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                    {
                        Log.Warning("Render: skipping segment {SegId} — asset not found or file missing (AssetId={AssetId}, Path={Path})",
                            segment.Id, segment.BackgroundAssetId, asset?.FilePath ?? "(null)");
                        continue;
                    }

                    var renderSeg = new RenderVisualSegment
                    {
                        SourcePath          = asset.FilePath,
                        StartTime           = segment.StartTime,
                        EndTime             = segment.EndTime,
                        IsVideo             = IsVideoAsset(asset),
                        SourceOffsetSeconds = 0
                    };

                    // Sync position and size from the linked canvas element when available.
                    // Without this, all images render at (0,0) full-canvas regardless of
                    // how the user has repositioned/resized them in the canvas preview.
                    var linkedElement = elements?.FirstOrDefault(e =>
                        string.Equals(e.SegmentId, segment.Id, StringComparison.Ordinal));

                    if (linkedElement != null && canvasWidth > 0 && canvasHeight > 0 && renderWidth > 0 && renderHeight > 0)
                    {
                        var scaleX = renderWidth  / canvasWidth;
                        var scaleY = renderHeight / canvasHeight;

                        var overlayX = (int)Math.Round(linkedElement.X * scaleX);
                        var overlayY = (int)Math.Round(linkedElement.Y * scaleY);
                        var scaleW   = (int)Math.Round(linkedElement.Width  * scaleX);
                        var scaleH   = (int)Math.Round(linkedElement.Height * scaleY);

                        // Clamp to valid render bounds
                        overlayX = Math.Max(0, Math.Min(overlayX, renderWidth  - 1));
                        overlayY = Math.Max(0, Math.Min(overlayY, renderHeight - 1));
                        scaleW   = Math.Max(1, Math.Min(scaleW,   renderWidth));
                        scaleH   = Math.Max(1, Math.Min(scaleH,   renderHeight));

                        renderSeg.OverlayX     = overlayX.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        renderSeg.OverlayY     = overlayY.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        renderSeg.ScaleWidth   = scaleW;
                        renderSeg.ScaleHeight  = scaleH;
                    }

                    segments.Add(renderSeg);
                }
            }

            return segments;
        }

        /// <summary>
        /// Rasterize text elements to PNG images (WYSIWYG) and return them as visual overlay segments.
        /// This ensures text wrapping, alignment, and styling in the export match the canvas preview exactly.
        /// </summary>
        private List<RenderVisualSegment> BuildRasterizedTextSegments(Project project, int renderWidth, int renderHeight)
        {
            var textTracks = project.Tracks?
                .Where(t => string.Equals(t.TrackType, "text", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                .OrderBy(t => t.Order)
                .ToList();

            if (textTracks == null || textTracks.Count == 0)
                return [];

            var canvasWidth = _canvasViewModel?.CanvasWidth ?? 0;
            var canvasHeight = _canvasViewModel?.CanvasHeight ?? 0;
            var elements = _canvasViewModel?.Elements;

            // Create temp directory for rasterized text images
            var textImageDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PodcastVideoEditor", "render_text_img");
            System.IO.Directory.CreateDirectory(textImageDir);

            var segments = new List<RenderVisualSegment>();
            var index = 0;
            foreach (var track in textTracks)
            {
                if (track.Segments == null) continue;
                foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
                {
                    if (string.IsNullOrWhiteSpace(segment.Text) || segment.EndTime <= segment.StartTime)
                        continue;

                    // Look up linked canvas element to get position/style/size data
                    var linkedElement = elements?.FirstOrDefault(e => e.SegmentId == segment.Id);

                    // Build rasterization options from element properties
                    var options = new TextRasterizeOptions
                    {
                        Text = segment.Text,
                        CanvasWidth = canvasWidth,
                        CanvasHeight = canvasHeight
                    };

                    int overlayX = 0, overlayY = 0;
                    int imgWidth = renderWidth, imgHeight = 80;

                    if (linkedElement != null && canvasWidth > 0 && canvasHeight > 0)
                    {
                        var scaleX = (double)renderWidth / canvasWidth;
                        var scaleY = (double)renderHeight / canvasHeight;

                        overlayX = (int)Math.Round(linkedElement.X * scaleX);
                        overlayY = (int)Math.Round(linkedElement.Y * scaleY);
                        imgWidth = Math.Max(1, (int)Math.Round(linkedElement.Width * scaleX));
                        imgHeight = Math.Max(1, (int)Math.Round(linkedElement.Height * scaleY));

                        // Clamp position
                        overlayX = Math.Max(0, Math.Min(overlayX, renderWidth - 1));
                        overlayY = Math.Max(0, Math.Min(overlayY, renderHeight - 1));

                        if (linkedElement is TitleElement title)
                        {
                            options.FontSize = (float)CoordinateMapper.ScaleFontSize(title.FontSize, canvasHeight, renderHeight);
                            options.ColorHex = title.ColorHex;
                            options.FontFamily = title.FontFamily;
                            options.IsBold = title.IsBold;
                            options.IsItalic = title.IsItalic;
                            options.Alignment = title.Alignment switch
                            {
                                Core.Models.TextAlignment.Left => TextRasterizeAlignment.Left,
                                Core.Models.TextAlignment.Right => TextRasterizeAlignment.Right,
                                _ => TextRasterizeAlignment.Center
                            };
                        }
                        else if (linkedElement is TextElement text)
                        {
                            options.FontSize = (float)CoordinateMapper.ScaleFontSize(text.FontSize, canvasHeight, renderHeight);
                            options.ColorHex = text.ColorHex;
                            options.Alignment = TextRasterizeAlignment.Center;
                        }
                    }
                    else
                    {
                        // No linked element — use defaults
                        options.FontSize = CoordinateMapper.ScaleFontSize(24, canvasHeight > 0 ? canvasHeight : renderHeight, renderHeight);
                        imgHeight = (int)(renderHeight * 0.1);
                        overlayY = (int)(renderHeight * 0.8);
                        overlayX = 0;
                        imgWidth = renderWidth;
                    }

                    options.Width = imgWidth;
                    options.Height = imgHeight;

                    // Rasterize text to PNG
                    var imagePath = System.IO.Path.Combine(textImageDir, $"text_{index}.png");
                    try
                    {
                        TextRasterizer.RenderToFile(options, imagePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to rasterize text segment {Index}, skipping", index);
                        index++;
                        continue;
                    }

                    segments.Add(new RenderVisualSegment
                    {
                        SourcePath  = imagePath,
                        StartTime   = segment.StartTime,
                        EndTime     = segment.EndTime,
                        IsVideo     = false,
                        OverlayX    = overlayX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OverlayY    = overlayY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ScaleWidth  = imgWidth,
                        ScaleHeight = imgHeight
                    });

                    index++;
                }
            }

            return segments;
        }

        /// <summary>
        /// Collect extra audio clip segments from all visible audio tracks.
        /// Fixes BUG-2: audio track clips were never mixed into the render output.
        /// </summary>
        private static List<RenderAudioSegment> BuildTimelineAudioSegments(Project project)
        {
            var audioTracks = project.Tracks?
                .Where(t => string.Equals(t.TrackType, "audio", StringComparison.OrdinalIgnoreCase) && t.IsVisible)
                .OrderBy(t => t.Order)
                .ToList();

            if (audioTracks == null || audioTracks.Count == 0)
                return [];

            var segments = new List<RenderAudioSegment>();
            foreach (var track in audioTracks)
            {
                if (track.Segments == null) continue;
                foreach (var segment in track.Segments.OrderBy(s => s.StartTime))
                {
                    if (segment.EndTime <= segment.StartTime || string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                        continue;

                    var asset = project.Assets?.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
                    if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath) || !System.IO.File.Exists(asset.FilePath))
                        continue;

                    segments.Add(new RenderAudioSegment
                    {
                        SourcePath          = asset.FilePath,
                        StartTime           = segment.StartTime,
                        EndTime             = segment.EndTime,
                        Volume              = segment.Volume,
                        FadeInDuration      = segment.FadeInDuration,
                        FadeOutDuration     = segment.FadeOutDuration,
                        SourceOffsetSeconds = segment.SourceStartOffset
                    });
                }
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
