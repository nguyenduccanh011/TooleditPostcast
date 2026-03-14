using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Converters;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for managing canvas elements and interactions.
    /// </summary>
    public partial class CanvasViewModel : ObservableObject, IDisposable
    {
        private readonly VisualizerViewModel? _visualizerViewModel;
        private readonly DispatcherTimer? _visualizerTimer;
        private readonly LRUCache<string, BitmapSource> _assetFrameCache = new(300); // Keep memory budget tighter for responsive GC
        private ProjectViewModel? _projectViewModel;
        private TimelineViewModel? _timelineViewModel;
        private SelectionSyncService? _selectionSyncService;
        private UndoRedoService? _undoRedo;
        private PropertyChangedEventHandler? _timelinePropertyChangedHandler;
        private PropertyChangedEventHandler? _projectPropertyChangedHandler;
        private PropertyChangedEventHandler? _audioPlayerPropertyChangedHandler;
        private bool _disposed;
        private string? _lastVisualSegmentId;
        private string? _lastVisualFrameKey;
        private readonly HashSet<string> _pendingVideoFrameRequests = new(StringComparer.Ordinal);
        private readonly object _pendingVideoFrameLock = new();

        // Performance: throttle canvas updates to ~60fps
        private DateTime _lastCanvasUpdateTime = DateTime.MinValue;
        private const int CanvasUpdateThrottleMs = 16; // ~60fps

        // Performance: dictionary cache for O(1) asset lookup instead of LINQ FirstOrDefault
        private Dictionary<string, Asset>? _assetDictionary;
        private int _assetDictionaryVersion;

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
        private Uri? videoSource;

        [ObservableProperty]
        private TimeSpan videoPosition;

        [ObservableProperty]
        private bool isVideoMode;

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
        /// Background image layers from all active visual tracks, ordered back-to-front (highest Track.Order first).
        /// Each entry is a BitmapSource for a non-video visual segment.
        /// The frontmost video (if any) is rendered via MediaElement, not included here.
        /// </summary>
        public ObservableCollection<BitmapSource> BackgroundLayers { get; } = new();

        /// <summary>
        /// All active text overlays from active text tracks, ordered by Track.Order (front first).
        /// </summary>
        public ObservableCollection<string> ActiveTextOverlays { get; } = new();

        /// <summary>
        /// Bitmap for visualizer elements on canvas. Updates ~30fps when audio is playing.
        /// </summary>
        [ObservableProperty]
        private WriteableBitmap? visualizerBitmapSource;

        [ObservableProperty]
        private string selectedAspectRatio = "9:16";

        // ✅ NEW: Playback properties for overlay controls
        [ObservableProperty]
        private string audioPlaybackTime = "00:00";

        [ObservableProperty]
        private string audioDuration = "00:00";

        [ObservableProperty]
        private bool isPlaying = false;

        private AudioPlayerViewModel? _audioPlayerViewModel;

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
            _timelineViewModel.ScriptApplied += OnScriptApplied;
            _projectViewModel.PropertyChanged += _projectPropertyChangedHandler;

            UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        /// <summary>
        /// Set selection sync service for bidirectional canvas↔timeline selection.
        /// </summary>
        public void SetSelectionSyncService(SelectionSyncService selectionSyncService)
        {
            _selectionSyncService = selectionSyncService;

            // When timeline segment is selected, highlight linked canvas elements
            _selectionSyncService.TimelineSelectionChanged += OnTimelineSegmentSelected;
        }

        /// <summary>Wire undo/redo. Called from MainViewModel after construction.</summary>
        public void SetUndoRedoService(UndoRedoService service) => _undoRedo = service;

        /// <summary>Expose so CanvasView can record element-move actions.</summary>
        public UndoRedoService? UndoRedoService => _undoRedo;

        private void OnTimelineSegmentSelected(string? segmentId, bool playheadInRange)
        {
            if (string.IsNullOrEmpty(segmentId))
            {
                // Deselect
                SelectElement(null);
                return;
            }

            // Highlight linked canvas element regardless of playhead position.
            // Matches commercial editor behavior: selecting a timeline segment always highlights its element.
            var linked = Elements.FirstOrDefault(e => string.Equals(e.SegmentId, segmentId, StringComparison.Ordinal));
            SelectElement(linked); // SelectElement(null) when no element is linked — deselects canvas
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
                X = Math.Max(0, (CanvasWidth - 400) / 2),
                Y = Math.Max(0, CanvasHeight * 0.08),
                Width = 400,
                Height = 100,
                ZIndex = Elements.Count
            };

            // Link to timeline segment so user can control visibility time range
            var segment = _timelineViewModel?.CreateSegmentForElement(TrackTypes.Text, element.Name);
            if (segment != null)
                element.SegmentId = segment.Id;

            Elements.Add(element);
            SelectElement(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            LogMessage($"Added Title element{(segment != null ? " + timeline segment" : "")}");
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
                X = Math.Max(0, CanvasWidth - 220),
                Y = 20,
                Width = 200,
                Height = 200,
                ZIndex = Elements.Count
            };

            var segment = _timelineViewModel?.CreateSegmentForElement(TrackTypes.Visual, element.Name);
            if (segment != null)
                element.SegmentId = segment.Id;

            Elements.Add(element);
            SelectElement(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            LogMessage($"Added Logo element{(segment != null ? " + timeline segment" : "")}");
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
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, (CanvasHeight - 400) / 2),
                Width = 600,
                Height = 400,
                ZIndex = Elements.Count
            };

            var segment = _timelineViewModel?.CreateSegmentForElement(TrackTypes.Visual, element.Name);
            if (segment != null)
                element.SegmentId = segment.Id;

            Elements.Add(element);
            SelectElement(element);
            EnsureVisualizerTimer();
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            LogMessage($"Added Visualizer element{(segment != null ? " + timeline segment" : "")}");
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
                X = Math.Max(0, (CanvasWidth - 300) / 2),
                Y = Math.Max(0, (CanvasHeight - 300) / 2),
                Width = 300,
                Height = 300,
                ZIndex = Elements.Count
            };

            var segment = _timelineViewModel?.CreateSegmentForElement(TrackTypes.Visual, element.Name);
            if (segment != null)
                element.SegmentId = segment.Id;

            Elements.Add(element);
            SelectElement(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            LogMessage($"Added Image element{(segment != null ? " + timeline segment" : "")}");
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
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, CanvasHeight - 160),
                Width = 600,
                Height = 80,
                ZIndex = Elements.Count
            };

            var segment = _timelineViewModel?.CreateSegmentForElement(TrackTypes.Text, element.Name);
            if (segment != null)
                element.SegmentId = segment.Id;

            Elements.Add(element);
            SelectElement(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            LogMessage($"Added Text element{(segment != null ? " + timeline segment" : "")}");
        }

        /// <summary>
        /// Called when ApplyScriptAsync completes. Removes canvas elements that were linked to
        /// the replaced text-track segments, then creates a new TextElement for every new segment
        /// so script text is editable/draggable instead of being a read-only subtitle overlay.
        /// </summary>
        private void OnScriptApplied(object? sender, ScriptAppliedEventArgs e)
        {
            // Remove canvas elements that belonged to the now-replaced text-track segments
            var orphaned = Elements
                .Where(el => el.SegmentId != null && e.ReplacedSegmentIds.Contains(el.SegmentId))
                .ToList();
            foreach (var old in orphaned)
                Elements.Remove(old);

            // Create an interactive TextElement for each new segment, positioned at subtitle area (center-bottom)
            double subtitleX = Math.Max(0, (CanvasWidth - 600) / 2);
            double subtitleY = Math.Max(0, CanvasHeight - 160);
            foreach (var segment in e.NewSegments)
            {
                var label = segment.Text.Length > 20 ? segment.Text[..20] + "…" : segment.Text;
                var element = new TextElement
                {
                    Name = label,
                    Content = segment.Text,
                    X = subtitleX,
                    Y = subtitleY,
                    Width = 600,
                    Height = 80,
                    ZIndex = Elements.Count,
                    SegmentId = segment.Id
                };
                Elements.Add(element);
            }

            LogMessage($"Canvas: created {e.NewSegments.Count} text element(s) from script");
            Log.Information("Canvas elements synced after script apply: {Count} new, {Removed} removed",
                e.NewSegments.Count, orphaned.Count);
        }

        /// <summary>
        /// Select an element on the canvas.
        /// Notifies SelectionSyncService for canvas→timeline sync.
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

                // Notify sync service: canvas element selected → highlight segment on timeline
                _selectionSyncService?.NotifyCanvasElementSelected(element.SegmentId);

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
            _undoRedo?.Record(new ElementDeletedAction(Elements, elementToDelete));
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
            _undoRedo?.Record(new ElementAddedAction(Elements, cloned));
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

        // ✅ NEW: Playback controls (wire to AudioPlayerViewModel)
        /// <summary>
        /// Attach audio player reference for playback control synchronization.
        /// Properly removes old handlers and adds new ones to prevent memory leaks.
        /// </summary>
        public void SetAudioPlayerViewModel(AudioPlayerViewModel audioPlayerViewModel)
        {
            ArgumentNullException.ThrowIfNull(audioPlayerViewModel, nameof(audioPlayerViewModel));

            // Unsubscribe from old audio player if exists
            if (_audioPlayerViewModel != null && _audioPlayerPropertyChangedHandler != null)
            {
                _audioPlayerViewModel.PropertyChanged -= _audioPlayerPropertyChangedHandler;
                _audioPlayerPropertyChangedHandler = null;
            }

            _audioPlayerViewModel = audioPlayerViewModel;

            // Create trackable event handler for new audio player
            _audioPlayerPropertyChangedHandler = OnAudioPlayerPropertyChanged;
            _audioPlayerViewModel.PropertyChanged += _audioPlayerPropertyChangedHandler;

            // Initialize display from audio player current state
            AudioPlaybackTime = _audioPlayerViewModel.PositionDisplay;
            AudioDuration = _audioPlayerViewModel.DurationDisplay;
            IsPlaying = _audioPlayerViewModel.IsPlaying;
        }

        /// <summary>
        /// Handles audio player property changes (position, duration, playing state).
        /// </summary>
        private void OnAudioPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_audioPlayerViewModel == null)
                return;

            switch (e.PropertyName)
            {
                case nameof(AudioPlayerViewModel.PositionDisplay):
                    AudioPlaybackTime = _audioPlayerViewModel.PositionDisplay;
                    break;
                case nameof(AudioPlayerViewModel.DurationDisplay):
                    AudioDuration = _audioPlayerViewModel.DurationDisplay;
                    break;
                case nameof(AudioPlayerViewModel.IsPlaying):
                    IsPlaying = _audioPlayerViewModel.IsPlaying;
                    break;
            }
        }

        /// <summary>
        /// Toggle play/pause: modern UI pattern (CapCut-style).
        /// Delegates to AudioPlayerViewModel's TogglePlayPauseCommand.
        /// </summary>
        [RelayCommand]
        public void TogglePlayPause()
        {
            try
            {
                _audioPlayerViewModel?.TogglePlayPauseCommand.Execute(null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error toggling play/pause");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Play audio from preview (kept for backward compatibility).
        /// Prefer TogglePlayPauseCommand for new code.
        /// </summary>
        [RelayCommand]
        public void Play()
        {
            _audioPlayerViewModel?.PlayCommand.Execute(null);
            StatusMessage = "▶ Playing...";
        }

        /// <summary>
        /// Pause audio playback (kept for backward compatibility).
        /// Prefer TogglePlayPauseCommand for new code.
        /// </summary>
        [RelayCommand]
        public void Pause()
        {
            _audioPlayerViewModel?.PauseCommand.Execute(null);
            StatusMessage = "⏸ Paused";
        }

        /// <summary>
        /// Stop audio playback (kept for backward compatibility).
        /// Prefer TogglePlayPauseCommand for new code.
        /// </summary>
        [RelayCommand]
        public void Stop()
        {
            _audioPlayerViewModel?.StopCommand.Execute(null);
            StatusMessage = "⏹ Stopped";
        }

        /// <summary>
        /// Set aspect ratio from menu (used by status bar ratio button).
        /// </summary>
        [RelayCommand]
        public void SetAspectRatio(string aspectRatio)
        {
            if (!string.IsNullOrWhiteSpace(aspectRatio) && AspectRatioOptions.Contains(aspectRatio))
                SelectedAspectRatio = aspectRatio;
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
            var normalizedAspect = RenderSizing.NormalizeAspectRatio(aspectRatio);
            var (previewWidth, previewHeight) = RenderSizing.ResolvePreviewSize(normalizedAspect, previewShortEdge: 1080);

            CanvasWidth = previewWidth;
            CanvasHeight = previewHeight;
            StatusMessage = $"Aspect ratio: {aspectRatio} ({(int)previewWidth}x{(int)previewHeight})";
        }

        private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_timelineViewModel == null || _projectViewModel == null)
                return;

            if (e.PropertyName == nameof(TimelineViewModel.PlayheadPosition))
            {
                var playhead = _timelineViewModel.PlayheadPosition;

                // Fast path: if in video mode, just update position (GPU-accelerated, very cheap)
                if (IsVideoMode && ActiveVisualSegment != null)
                {
                    // Check if playhead is still within the same segment
                    if (playhead >= ActiveVisualSegment.StartTime && playhead < ActiveVisualSegment.EndTime)
                    {
                        var segmentOffset = playhead - ActiveVisualSegment.StartTime;
                        VideoPosition = TimeSpan.FromSeconds(Math.Max(0, segmentOffset));
                        return; // Skip full UpdateActivePreview — no segment change
                    }
                }

                // Throttle full canvas updates to ~60fps
                var now = DateTime.UtcNow;
                if ((now - _lastCanvasUpdateTime).TotalMilliseconds < CanvasUpdateThrottleMs)
                    return;
                _lastCanvasUpdateTime = now;

                UpdateActivePreview(playhead);
            }
        }

        private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectViewModel.CurrentProject))
            {
                _assetFrameCache.Clear();
                _assetDictionary = null; // Invalidate asset dictionary
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

            // --- Collect ALL visual and text segments (multi-track composite) ---
            // GetActiveSegmentsAtTime already filters by Track.IsVisible and sorts by Track.Order (0=front)
            var visualPairs = new List<(Track track, Segment segment)>();
            var textPairs = new List<(Track track, Segment segment)>();

            foreach (var pair in active)
            {
                if (string.Equals(pair.track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase))
                    visualPairs.Add(pair);
                else if (string.Equals(pair.track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    textPairs.Add(pair);
                // Audio type segments are not visual — skip for preview rendering
            }

            // --- Multi-text composite: collect text from ALL active text tracks ---
            ActiveTextOverlays.Clear();
            foreach (var tp in textPairs)
            {
                if (!string.IsNullOrWhiteSpace(tp.segment.Text))
                    ActiveTextOverlays.Add(tp.segment.Text);
            }

            // Legacy single-text properties (kept for backward compatibility)
            var primaryTextPair = textPairs.Count > 0 ? textPairs[0] : default;
            ActiveTextSegment = primaryTextPair.segment;
            ActiveTextContent = string.Join("\n", ActiveTextOverlays);
            IsTextOverlayVisible = ActiveTextOverlays.Count > 0;

            // --- Multi-visual composite: iterate visual tracks by z-order ---
            // Strategy: frontmost video → MediaElement; other images → BackgroundLayers
            var primaryVisualPair = visualPairs.Count > 0 ? visualPairs[0] : default;
            ActiveVisualSegment = primaryVisualPair.segment;

            bool videoHandled = false;
            BackgroundLayers.Clear();

            // Try to find a video asset in the frontmost visual segment
            if (primaryVisualPair.segment != null && !string.IsNullOrWhiteSpace(primaryVisualPair.segment.BackgroundAssetId))
            {
                var asset = FindAssetById(primaryVisualPair.segment.BackgroundAssetId);
                if (asset != null && IsVideoAsset(asset))
                {
                    if (!string.IsNullOrWhiteSpace(asset.FilePath) && File.Exists(asset.FilePath))
                    {
                        try
                        {
                            var newUri = new Uri(asset.FilePath, UriKind.Absolute);
                            if (VideoSource?.LocalPath != newUri.LocalPath)
                            {
                                VideoSource = newUri;
                                Serilog.Log.Information("Video source CHANGED: {Path}", asset.FilePath);
                            }
                            var segmentOffset = playheadSeconds - primaryVisualPair.segment.StartTime;
                            VideoPosition = TimeSpan.FromSeconds(Math.Max(0, segmentOffset));
                            IsVideoMode = true;
                            videoHandled = true;
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "Failed to set video source: {Path}", asset.FilePath);
                        }
                    }
                }
            }

            if (!videoHandled)
            {
                IsVideoMode = false;
                VideoSource = null;
            }

            // Load image layers for all visual segments (skip the one handled as video)
            // Add from back to front: highest Order (background) first in collection
            for (int i = visualPairs.Count - 1; i >= 0; i--)
            {
                var (track, segment) = visualPairs[i];

                // If this is the primary video segment already handled by MediaElement, skip
                if (videoHandled && i == 0)
                    continue;

                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                    continue;

                var layerAsset = FindAssetById(segment.BackgroundAssetId);
                if (layerAsset == null)
                    continue;

                var layerFrame = LoadFrameForAsset(layerAsset, segment, playheadSeconds);
                if (layerFrame != null)
                    BackgroundLayers.Add(layerFrame);
            }

            // Legacy single-image property: set to frontmost non-video visual (or null)
            if (!videoHandled && visualPairs.Count > 0)
            {
                var frontAsset = FindAssetById(primaryVisualPair.segment?.BackgroundAssetId);
                ActiveVisualImage = frontAsset != null
                    ? LoadFrameForAsset(frontAsset, primaryVisualPair.segment!, playheadSeconds)
                    : null;
            }
            else if (!videoHandled)
            {
                ActiveVisualImage = null;
            }

            IsVisualPlaceholderVisible = ActiveVisualSegment == null && BackgroundLayers.Count == 0 && !IsVideoMode;
            UpdateElementVisibility(active);
        }

        /// <summary>
        /// Check if an asset is a video file (by Type or file extension).
        /// </summary>
        private static bool IsVideoAsset(Asset asset)
        {
            if (string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrWhiteSpace(asset.FilePath))
                return false;
            return asset.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   asset.FilePath.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
                   asset.FilePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                   asset.FilePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   asset.FilePath.EndsWith(".wmv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Update visibility of canvas elements based on their SegmentId.
        /// Elements with SegmentId=null are always visible (global overlays).
        /// Elements with a SegmentId are only visible when that segment is active at the current playhead.
        /// </summary>
        private void UpdateElementVisibility(List<(Track track, Segment segment)> activeSegments)
        {
            if (Elements.Count == 0)
                return;

            // Build a set of active segment IDs for O(1) lookup
            HashSet<string>? activeSegmentIds = null;
            foreach (var (_, seg) in activeSegments)
            {
                if (seg != null)
                {
                    activeSegmentIds ??= new HashSet<string>(StringComparer.Ordinal);
                    activeSegmentIds.Add(seg.Id);
                }
            }

            foreach (var element in Elements)
            {
                if (string.IsNullOrEmpty(element.SegmentId))
                {
                    // Global overlay — always visible
                    element.IsVisible = true;
                }
                else
                {
                    // Segment-bound — visible only when segment is active
                    element.IsVisible = activeSegmentIds != null && activeSegmentIds.Contains(element.SegmentId);
                }
            }
        }

        private Asset? FindAssetById(string? assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                return null;

            var assets = _projectViewModel?.CurrentProject?.Assets;
            if (assets == null)
                return null;

            // Use dictionary cache for O(1) lookup instead of LINQ FirstOrDefault O(n)
            if (_assetDictionary == null || _assetDictionaryVersion != assets.Count)
            {
                _assetDictionary = new Dictionary<string, Asset>(assets.Count, StringComparer.Ordinal);
                foreach (var a in assets)
                {
                    if (!string.IsNullOrWhiteSpace(a.Id))
                        _assetDictionary[a.Id] = a;
                }
                _assetDictionaryVersion = assets.Count;
            }

            return _assetDictionary.TryGetValue(assetId, out var found) ? found : null;
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

            var thumbPath = FFmpegService.GetThumbnailCachePathFor(asset.FilePath, quantized);
            if (!File.Exists(thumbPath))
            {
                QueueVideoFrameGeneration(asset.FilePath, quantized, frameKey, segment.Id);
                return ActiveVisualImage;
            }

            var bmpFrame = LoadBitmapFromPath(thumbPath, frameKey);
            if (bmpFrame != null)
            {
                _lastVisualSegmentId = segment.Id;
                _lastVisualFrameKey = frameKey;
            }
            return bmpFrame;
        }

        private void QueueVideoFrameGeneration(string videoPath, double timeInVideo, string frameKey, string? segmentId)
        {
            lock (_pendingVideoFrameLock)
            {
                if (!_pendingVideoFrameRequests.Add(frameKey))
                    return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var generated = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(videoPath, timeInVideo).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(generated) || !File.Exists(generated))
                        return;

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var bmp = LoadBitmapFromPath(generated, frameKey);
                        if (bmp == null || ActiveVisualSegment == null)
                            return;

                        if (!string.Equals(ActiveVisualSegment.Id, segmentId, StringComparison.Ordinal))
                            return;

                        ActiveVisualImage = bmp;
                        _lastVisualSegmentId = segmentId;
                        _lastVisualFrameKey = frameKey;
                        IsVisualPlaceholderVisible = false;
                    });
                }
                catch
                {
                    // Ignore background thumbnail failures to keep scrubbing smooth.
                }
                finally
                {
                    lock (_pendingVideoFrameLock)
                        _pendingVideoFrameRequests.Remove(frameKey);
                }
            });
        }

        private BitmapSource? LoadBitmapFromPath(string path, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            // Check LRU cache first
            if (_assetFrameCache.TryGet(cacheKey, out var cached))
                return cached;

            try
            {
                var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = 960;
                bmp.EndInit();
                bmp.Freeze();

                // Add to LRU cache (auto-evicts if full)
                _assetFrameCache.Add(cacheKey, bmp);

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
            VideoSource = null;
            IsVideoMode = false;
            ActiveTextContent = string.Empty;
            ActiveVisualSegment = null;
            ActiveTextSegment = null;
            IsTextOverlayVisible = false;
            IsVisualPlaceholderVisible = true;
            BackgroundLayers.Clear();
            ActiveTextOverlays.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                // Unsubscribe from property changed events
                if (_timelineViewModel != null && _timelinePropertyChangedHandler != null)
                    _timelineViewModel.PropertyChanged -= _timelinePropertyChangedHandler;
                if (_timelineViewModel != null)
                    _timelineViewModel.ScriptApplied -= OnScriptApplied;
                if (_projectViewModel != null && _projectPropertyChangedHandler != null)
                    _projectViewModel.PropertyChanged -= _projectPropertyChangedHandler;

                // Unsubscribe from audio player
                if (_audioPlayerViewModel != null && _audioPlayerPropertyChangedHandler != null)
                {
                    _audioPlayerViewModel.PropertyChanged -= _audioPlayerPropertyChangedHandler;
                    _audioPlayerPropertyChangedHandler = null;
                }

                // Stop and dispose visualizer timer
                if (_visualizerTimer != null)
                {
                    _visualizerTimer.Stop();
                    _visualizerTimer.Tick -= OnVisualizerTimerTick;
                }

                PropertyEditor?.Dispose();
                _pendingVideoFrameRequests.Clear();
                _assetFrameCache.Clear();
                Elements.Clear();

                Serilog.Log.Debug("CanvasViewModel disposed");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error during CanvasViewModel disposal");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Save canvas elements to the database via ProjectService.
        /// Converts CanvasElements → Elements and replaces all elements in the project.
        /// </summary>
        public async Task SaveElementsAsync(ProjectService projectService)
        {
            if (_projectViewModel?.CurrentProject == null)
            {
                LogMessage("No project loaded - cannot save elements");
                return;
            }

            try
            {
                var projectId = _projectViewModel.CurrentProject.Id;
                var dbElements = ElementMapper.ToElements(Elements, projectId);
                await projectService.ReplaceElementsAsync(projectId, dbElements);
                LogMessage($"Saved {dbElements.Count} element(s) to project");
                Log.Information("Canvas elements saved: {Count} for project {ProjectId}", dbElements.Count, projectId);
            }
            catch (Exception ex)
            {
                LogMessage($"Error saving elements: {ex.Message}");
                Log.Error(ex, "Error saving canvas elements");
            }
        }

        /// <summary>
        /// Load canvas elements from the current project's Elements collection.
        /// Converts DB Elements → CanvasElements and populates the canvas.
        /// </summary>
        public void LoadElementsFromProject()
        {
            if (_projectViewModel?.CurrentProject == null)
            {
                return;
            }

            try
            {
                var projectElements = _projectViewModel.CurrentProject.Elements;
                if (projectElements == null || projectElements.Count == 0)
                {
                    Elements.Clear();
                    SelectedElement = null;
                    PropertyEditor.SetSelectedElement(null);
                    return;
                }

                var canvasElements = ElementMapper.ToCanvasElements(projectElements);
                Elements.Clear();
                foreach (var ce in canvasElements)
                    Elements.Add(ce);

                SelectedElement = null;
                PropertyEditor.SetSelectedElement(null);
                EnsureVisualizerTimer();
                LogMessage($"Loaded {canvasElements.Count} element(s) from project");
                Log.Information("Canvas elements loaded: {Count} from project {ProjectId}",
                    canvasElements.Count, _projectViewModel.CurrentProject.Id);
            }
            catch (Exception ex)
            {
                LogMessage($"Error loading elements: {ex.Message}");
                Log.Error(ex, "Error loading canvas elements from project");
            }
        }

    }
}
