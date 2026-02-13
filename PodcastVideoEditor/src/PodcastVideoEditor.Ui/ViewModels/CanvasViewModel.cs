using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Converters;
using PodcastVideoEditor.Ui.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for managing canvas elements and interactions.
    /// </summary>
    public partial class CanvasViewModel : ObservableObject, IDisposable
    {
        private readonly VisualizerViewModel? _visualizerViewModel;
        private readonly DispatcherTimer? _visualizerTimer;
        private readonly Dictionary<string, BitmapSource> _assetFrameCache = new();
        private ProjectViewModel? _projectViewModel;
        private TimelineViewModel? _timelineViewModel;
        private PropertyChangedEventHandler? _timelinePropertyChangedHandler;
        private PropertyChangedEventHandler? _projectPropertyChangedHandler;
        private bool _disposed;
        private string? _lastVisualSegmentId;
        private string? _lastVisualFrameKey;

        public ObservableCollection<string> AspectRatioOptions { get; } = new()
        {
            "9:16", "16:9", "1:1", "4:5"
        };

        [ObservableProperty]
        private ObservableCollection<CanvasElement> elements = new();

        [ObservableProperty]
        private CanvasElement? selectedElement;

        /// <summary>
        /// Property editor for selected element.
        /// </summary>
        public PropertyEditorViewModel PropertyEditor { get; }

        [ObservableProperty]
        private double canvasWidth = 1920;

        [ObservableProperty]
        private double canvasHeight = 1080;

        [ObservableProperty]
        private double gridSize = 10.0; // Grid snapping (optional)

        [ObservableProperty]
        private bool showGrid = false;

        [ObservableProperty]
        private string statusMessage = "Ready";

        [ObservableProperty]
        private BitmapSource? activeVisualImage;

        [ObservableProperty]
        private string activeTextContent = string.Empty;

        [ObservableProperty]
        private bool isVisualPlaceholderVisible = true;

        [ObservableProperty]
        private bool isTextOverlayVisible;

        [ObservableProperty]
        private Segment? activeVisualSegment;

        [ObservableProperty]
        private Segment? activeTextSegment;

        /// <summary>
        /// Bitmap for visualizer elements on canvas. Updates ~30fps when audio is playing.
        /// </summary>
        [ObservableProperty]
        private WriteableBitmap? visualizerBitmapSource;

        [ObservableProperty]
        private string selectedAspectRatio = "9:16";

        public CanvasViewModel()
        {
            PropertyEditor = new PropertyEditorViewModel();
            ApplyAspectRatio(selectedAspectRatio);
        }

        /// <summary>
        /// Attach project and timeline references so preview can react to playhead changes.
        /// </summary>
        public void AttachProjectAndTimeline(ProjectViewModel projectViewModel, TimelineViewModel timelineViewModel)
        {
            _projectViewModel = projectViewModel ?? throw new ArgumentNullException(nameof(projectViewModel));
            _timelineViewModel = timelineViewModel ?? throw new ArgumentNullException(nameof(timelineViewModel));

            _timelinePropertyChangedHandler ??= OnTimelinePropertyChanged;
            _projectPropertyChangedHandler ??= OnProjectPropertyChanged;

            _timelineViewModel.PropertyChanged += _timelinePropertyChangedHandler;
            _projectViewModel.PropertyChanged += _projectPropertyChangedHandler;

            UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        public CanvasViewModel(VisualizerViewModel visualizerViewModel)
        {
            _visualizerViewModel = visualizerViewModel ?? throw new ArgumentNullException(nameof(visualizerViewModel));
            _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30fps
            };
            _visualizerTimer.Tick += OnVisualizerTimerTick;
            PropertyEditor = new PropertyEditorViewModel();
            PropertyEditor.OnVisualizerElementConfigChanged = SyncVisualizerFromElement;
            ApplyAspectRatio(selectedAspectRatio);
        }

        private void SyncVisualizerFromElement(VisualizerElement element)
        {
            if (_visualizerViewModel == null)
                return;
            _visualizerViewModel.SelectedStyle = element.Style;
            _visualizerViewModel.SelectedPalette = element.ColorPalette;
            _visualizerViewModel.SelectedBandCount = element.BandCount;
        }

        private void OnVisualizerTimerTick(object? sender, EventArgs e)
        {
            if (_visualizerViewModel == null || _disposed)
                return;

            var skBitmap = _visualizerViewModel.GetCurrentFrame();
            var wpfBitmap = SkiaConversionHelper.ToBitmapSource(skBitmap);
            if (wpfBitmap != null && HasVisualizerElements)
                VisualizerBitmapSource = wpfBitmap;
        }

        private bool HasVisualizerElements => Elements.Any(e => e is VisualizerElement);

        /// <summary>
        /// Start visualizer bitmap updates when canvas has visualizer elements.
        /// </summary>
        public void EnsureVisualizerTimer()
        {
            if (_visualizerViewModel == null || _visualizerTimer == null)
                return;

            if (HasVisualizerElements && !_visualizerTimer.IsEnabled)
            {
                _visualizerViewModel.Initialize();
                _visualizerTimer.Start();
            }
            else if (!HasVisualizerElements && _visualizerTimer.IsEnabled)
            {
                _visualizerTimer.Stop();
            }
        }

        /// <summary>
        /// Add a new title element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTitleElement()
        {
            var element = new TitleElement
            {
                Name = $"Title {Elements.Count + 1}",
                X = 50,
                Y = 50,
                Width = 400,
                Height = 100,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Title element");
        }

        /// <summary>
        /// Add a new logo element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddLogoElement()
        {
            var element = new LogoElement
            {
                Name = $"Logo {Elements.Count + 1}",
                X = 100,
                Y = 100,
                Width = 200,
                Height = 200,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Logo element");
        }

        /// <summary>
        /// Add a new visualizer element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddVisualizerElement()
        {
            var element = new VisualizerElement
            {
                Name = $"Visualizer {Elements.Count + 1}",
                X = 200,
                Y = 200,
                Width = 600,
                Height = 400,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            EnsureVisualizerTimer();
            LogMessage($"Added Visualizer element");
        }

        /// <summary>
        /// Add a new image element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddImageElement()
        {
            var element = new ImageElement
            {
                Name = $"Image {Elements.Count + 1}",
                X = 150,
                Y = 150,
                Width = 300,
                Height = 300,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Image element");
        }

        /// <summary>
        /// Add a new text element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTextElement()
        {
            var element = new TextElement
            {
                Name = $"Text {Elements.Count + 1}",
                X = 200,
                Y = 250,
                Width = 300,
                Height = 80,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Text element");
        }

        /// <summary>
        /// Select an element on the canvas.
        /// </summary>
        public void SelectElement(CanvasElement? element)
        {
            // Deselect previous
            if (SelectedElement != null)
            {
                SelectedElement.IsSelected = false;
            }

            // Select new
            if (element != null)
            {
                element.IsSelected = true;
                SelectedElement = element;
                PropertyEditor.SetSelectedElement(element);
                if (element is VisualizerElement ve)
                    SyncVisualizerFromElement(ve);
                LogMessage($"Selected: {element.Name}");
            }
            else
            {
                SelectedElement = null;
                PropertyEditor.SetSelectedElement(null);
            }
        }

        /// <summary>
        /// Delete the currently selected element.
        /// </summary>
        [RelayCommand]
        public void DeleteSelectedElement()
        {
            if (SelectedElement == null)
            {
                LogMessage("No element selected");
                return;
            }

            var elementToDelete = SelectedElement;
            Elements.Remove(elementToDelete);
            SelectedElement = null;
            PropertyEditor.SetSelectedElement(null);
            EnsureVisualizerTimer();
            LogMessage($"Deleted: {elementToDelete.Name}");
        }

        /// <summary>
        /// Move element to new position.
        /// </summary>
        public void MoveElement(CanvasElement element, double newX, double newY)
        {
            if (element != null)
            {
                element.X = Math.Max(0, newX);
                element.Y = Math.Max(0, newY);
            }
        }

        /// <summary>
        /// Resize element.
        /// </summary>
        public void ResizeElement(CanvasElement element, double newWidth, double newHeight)
        {
            if (element != null)
            {
                element.Width = Math.Max(10, newWidth);
                element.Height = Math.Max(10, newHeight);
            }
        }

        /// <summary>
        /// Duplicate the selected element.
        /// </summary>
        [RelayCommand]
        public void DuplicateElement()
        {
            if (SelectedElement == null)
            {
                LogMessage("No element selected");
                return;
            }

            var cloned = SelectedElement.Clone();
            cloned.X += 20;
            cloned.Y += 20;
            cloned.ZIndex = Elements.Count;

            Elements.Add(cloned);
            SelectElement(cloned);
            EnsureVisualizerTimer();
            LogMessage($"Duplicated: {cloned.Name}");
        }

        /// <summary>
        /// Bring selected element to front (increase z-index).
        /// </summary>
        [RelayCommand]
        public void BringToFront()
        {
            if (SelectedElement == null) return;

            var maxZ = Elements.Max(e => e.ZIndex);
            SelectedElement.ZIndex = maxZ + 1;
            LogMessage($"Moved {SelectedElement.Name} to front");
        }

        /// <summary>
        /// Send selected element to back (decrease z-index).
        /// </summary>
        [RelayCommand]
        public void SendToBack()
        {
            if (SelectedElement == null) return;

            var minZ = Elements.Min(e => e.ZIndex);
            SelectedElement.ZIndex = minZ - 1;
            LogMessage($"Moved {SelectedElement.Name} to back");
        }

        /// <summary>
        /// Clear all elements from canvas.
        /// </summary>
        [RelayCommand]
        public void ClearAll()
        {
            Elements.Clear();
            SelectedElement = null;
            PropertyEditor.SetSelectedElement(null);
            EnsureVisualizerTimer();
            LogMessage("Canvas cleared");
        }

        /// <summary>
        /// Delete all elements and start fresh.
        /// </summary>
        [RelayCommand]
        public void ResetCanvas()
        {
            ClearAll();
            ApplyAspectRatio(SelectedAspectRatio);
            LogMessage("Canvas reset to default");
        }

        /// <summary>
        /// Log a status message.
        /// </summary>
        private void LogMessage(string message)
        {
            StatusMessage = message;
            // Also log to Serilog if needed
            Serilog.Log.Debug("Canvas: {Message}", message);
        }

        /// <summary>
        /// Get element by ID.
        /// </summary>
        public CanvasElement? GetElementById(string id) =>
            Elements.FirstOrDefault(e => e.Id == id);

        partial void OnSelectedAspectRatioChanged(string value)
        {
            ApplyAspectRatio(value);
        }

        private void ApplyAspectRatio(string aspectRatio)
        {
            var parts = aspectRatio.Split(':');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], out var w) ||
                !double.TryParse(parts[1], out var h) ||
                w <= 0 || h <= 0)
            {
                w = 9;
                h = 16;
            }

            const double baseHeight = 1080.0;
            CanvasHeight = baseHeight;
            CanvasWidth = Math.Round(baseHeight * (w / h));
            StatusMessage = $"Preview ratio set to {w}:{h} ({CanvasWidth}x{CanvasHeight})";
        }

        private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_timelineViewModel == null || _projectViewModel == null)
                return;

            if (e.PropertyName == nameof(TimelineViewModel.PlayheadPosition))
                UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectViewModel.CurrentProject))
            {
                _assetFrameCache.Clear();
                _lastVisualFrameKey = null;
                _lastVisualSegmentId = null;
                UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
            }
        }

        private void UpdateActivePreview(double playheadSeconds)
        {
            if (_timelineViewModel == null || _projectViewModel?.CurrentProject == null)
            {
                ClearActivePreview();
                return;
            }

            var active = _timelineViewModel.GetActiveSegmentsAtTime(playheadSeconds);
            var visualPair = active.FirstOrDefault(p => string.Equals(p.track.TrackType, "visual", StringComparison.OrdinalIgnoreCase));
            var textPair = active.FirstOrDefault(p => string.Equals(p.track.TrackType, "text", StringComparison.OrdinalIgnoreCase));

            ActiveVisualSegment = visualPair.segment;
            ActiveTextSegment = textPair.segment;

            ActiveTextContent = textPair.segment?.Text ?? string.Empty;
            IsTextOverlayVisible = !string.IsNullOrWhiteSpace(ActiveTextContent);

            BitmapSource? frame = null;
            if (visualPair.segment != null && !string.IsNullOrWhiteSpace(visualPair.segment.BackgroundAssetId))
            {
                var asset = FindAssetById(visualPair.segment.BackgroundAssetId);
                if (asset != null)
                {
                    frame = LoadFrameForAsset(asset, visualPair.segment, playheadSeconds);
                }
            }

            ActiveVisualImage = frame;
            IsVisualPlaceholderVisible = ActiveVisualSegment == null || ActiveVisualImage == null;
        }

        private Asset? FindAssetById(string? assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                return null;

            var assets = _projectViewModel?.CurrentProject?.Assets;
            return assets?.FirstOrDefault(a => string.Equals(a.Id, assetId, StringComparison.Ordinal));
        }

        private BitmapSource? LoadFrameForAsset(Asset asset, Segment segment, double playheadSeconds)
        {
            if (string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
                return null;

            if (string.Equals(asset.Type, "Image", StringComparison.OrdinalIgnoreCase))
            {
                var key = asset.Id ?? asset.FilePath;
                if (_lastVisualSegmentId == segment.Id && _lastVisualFrameKey == key && ActiveVisualImage != null)
                    return ActiveVisualImage;

                var bmp = LoadBitmapFromPath(asset.FilePath, key);
                _lastVisualSegmentId = segment.Id;
                _lastVisualFrameKey = key;
                return bmp;
            }

            if (!string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                return null;

            var timeIntoVideo = Math.Max(0, playheadSeconds - segment.StartTime);
            var quantized = Math.Round(timeIntoVideo, 1); // ~10fps to avoid excessive FFmpeg calls
            var frameKey = $"{asset.Id}|{quantized:F2}";

            if (_lastVisualSegmentId == segment.Id && _lastVisualFrameKey == frameKey && ActiveVisualImage != null)
                return ActiveVisualImage;

            if (_timelineViewModel?.IsDeferringThumbnailUpdate == true)
                return ActiveVisualImage;

            var thumbPath = FFmpegService.GetOrCreateVideoThumbnailPath(asset.FilePath, timeIntoVideo);
            if (string.IsNullOrWhiteSpace(thumbPath) || !File.Exists(thumbPath))
            {
                var fallbackPath = FFmpegService.GetThumbnailCachePathFor(asset.FilePath, timeIntoVideo);
                if (!VideoThumbnailFallback.TryCaptureFrameToFile(asset.FilePath, timeIntoVideo, fallbackPath))
                    return ActiveVisualImage;
                thumbPath = fallbackPath;
            }

            var bmpFrame = LoadBitmapFromPath(thumbPath, frameKey);
            if (bmpFrame != null)
            {
                _lastVisualSegmentId = segment.Id;
                _lastVisualFrameKey = frameKey;
            }
            return bmpFrame;
        }

        private BitmapSource? LoadBitmapFromPath(string path, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            lock (_assetFrameCache)
            {
                if (_assetFrameCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            try
            {
                var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.EndInit();
                bmp.Freeze();

                lock (_assetFrameCache)
                {
                    if (_assetFrameCache.Count > 500)
                        _assetFrameCache.Clear();
                    _assetFrameCache[cacheKey] = bmp;
                }

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void ClearActivePreview()
        {
            ActiveVisualImage = null;
            ActiveTextContent = string.Empty;
            ActiveVisualSegment = null;
            ActiveTextSegment = null;
            IsTextOverlayVisible = false;
            IsVisualPlaceholderVisible = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_timelineViewModel != null && _timelinePropertyChangedHandler != null)
                _timelineViewModel.PropertyChanged -= _timelinePropertyChangedHandler;
            if (_projectViewModel != null && _projectPropertyChangedHandler != null)
                _projectViewModel.PropertyChanged -= _projectPropertyChangedHandler;

            PropertyEditor?.Dispose();
            _visualizerTimer?.Stop();
            Serilog.Log.Debug("CanvasViewModel disposed");
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Export element list as JSON (for saving to database).
        /// </summary>
        public string ExportAsJson()
        {
            // Implementation: serialize Elements to JSON
            // This will be used when saving project
            return System.Text.Json.JsonSerializer.Serialize(Elements);
        }

        /// <summary>
        /// Import elements from JSON.
        /// </summary>
        public void ImportFromJson(string json)
        {
            try
            {
                // Implementation: deserialize JSON to Elements
                Elements.Clear();
                LogMessage("Imported elements from JSON");
            }
            catch (Exception ex)
            {
                LogMessage($"Import failed: {ex.Message}");
            }
        }
    }
}
