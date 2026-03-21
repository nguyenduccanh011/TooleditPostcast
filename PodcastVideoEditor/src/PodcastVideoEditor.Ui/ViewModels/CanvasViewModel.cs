using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Converters;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
        private readonly LRUCache<string, BitmapSource> _assetFrameCache = new(120, global::PodcastVideoEditor.Ui.Helpers.BitmapCacheMetrics.EstimateBitmapBytes, 96L * 1024 * 1024);
        private ProjectViewModel? _projectViewModel;
        private TimelineViewModel? _timelineViewModel;
        private SelectionSyncService? _selectionSyncService;
        private UndoRedoService? _undoRedo;
        private ObservableCollection<Track>? _trackedTracks;
        private HashSet<Segment>? _trackedSegments;
        private NotifyCollectionChangedEventHandler? _tracksCollectionChangedHandler;
        private PropertyChangedEventHandler? _trackPropertyChangedHandler;
        private PropertyChangedEventHandler? _segmentPropertyChangedHandler;
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
        private long _assetDictionarySignature;

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
        public ObservableCollection<PreviewVisualLayer> BackgroundLayers { get; } = new();

        [ObservableProperty]
        private double activeVisualX;

        [ObservableProperty]
        private double activeVisualY;

        [ObservableProperty]
        private double activeVisualWidth = 1920;

        [ObservableProperty]
        private double activeVisualHeight = 1080;

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

        /// <summary>
        /// Mirrors TimelineViewModel.IsLoopPlayback. Persisted to the timeline VM via ToggleLoopPlayback.
        /// Exposed here so the Loop button in CanvasView can bind directly to CanvasViewModel.
        /// </summary>
        [ObservableProperty]
        private bool isLoopPlayback = false;

        private AudioPlayerViewModel? _audioPlayerViewModel;

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
                SelectElement(null);
                return;
            }

            // Highlight linked canvas element regardless of playhead position.
            var linked = Elements.FirstOrDefault(e => string.Equals(e.SegmentId, segmentId, StringComparison.Ordinal));

            // If no linked element exists, auto-create one for image or text segments
            if (linked == null)
                linked = (CanvasElement?)TryCreateImageElementForSegment(segmentId)
                      ?? TryCreateTextElementForSegment(segmentId);

            SelectElement(linked);
        }

        /// <summary>
        /// When the user selects a visual-track image segment that has no linked CanvasElement,
        /// create an ImageElement for it (full-canvas size) so the user can reposition/resize it.
        /// </summary>
        private ImageElement? TryCreateImageElementForSegment(string segmentId)
        {
            if (_timelineViewModel == null || _projectViewModel?.CurrentProject == null)
                return null;

            // Find the segment across all tracks
            var segment = _timelineViewModel.Tracks
                .SelectMany(t => t.Segments)
                .FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.Ordinal));

            if (segment == null || string.IsNullOrEmpty(segment.BackgroundAssetId))
                return null;

            // Only for image assets (not audio/video)
            var asset = FindAssetById(segment.BackgroundAssetId);
            if (asset == null || IsVideoAsset(asset) || string.IsNullOrWhiteSpace(asset.FilePath))
                return null;
            if (string.Equals(asset.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                return null;

            // Create an ImageElement at full canvas size to match the current background rendering.
            // The user can then drag/resize it freely.
            var element = new ImageElement
            {
                Name = asset.Name ?? Path.GetFileNameWithoutExtension(asset.FilePath) ?? "Image",
                FilePath = asset.FilePath,
                X = 0,
                Y = 0,
                Width = CanvasWidth,
                Height = CanvasHeight,
                ZIndex = Elements.Count,
                SegmentId = segmentId
            };

            Elements.Add(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            Log.Information("Auto-created ImageElement for visual segment {SegmentId}", segmentId);
            return element;
        }

        /// <summary>
        /// Called from UpdateActivePreview: proactively creates an interactive TextElement
        /// for a segment that has no linked canvas element yet, so text always renders
        /// as a draggable element instead of the read-only overlay.
        /// </summary>
        private void EnsureTextElementForSegment(Segment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.Id) || string.IsNullOrWhiteSpace(segment.Text))
                return;

            // Double-check no element exists (race guard)
            if (Elements.Any(e => string.Equals(e.SegmentId, segment.Id, StringComparison.Ordinal)))
                return;

            var label = segment.Text.Length > 20 ? segment.Text[..20] + "…" : segment.Text;
            var element = new TextElement
            {
                Name = label,
                Content = segment.Text,
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, CanvasHeight - 160),
                Width = 600,
                Height = 80,
                ZIndex = Elements.Count,
                SegmentId = segment.Id
            };

            Elements.Add(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            Log.Information("Auto-created TextElement for text segment {SegmentId} during preview", segment.Id);
        }

        /// <summary>
        /// When the user selects a text-track segment that has no linked CanvasElement,
        /// create a TextElement so the user can drag/resize it on the preview canvas.
        /// </summary>
        private TextElement? TryCreateTextElementForSegment(string segmentId)
        {
            if (_timelineViewModel == null || _projectViewModel?.CurrentProject == null)
                return null;

            // Find the segment and verify it's on a text track
            Track? ownerTrack = null;
            Segment? segment = null;
            foreach (var track in _timelineViewModel.Tracks)
            {
                if (!string.Equals(track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    continue;
                var seg = track.Segments.FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.Ordinal));
                if (seg != null)
                {
                    ownerTrack = track;
                    segment = seg;
                    break;
                }
            }

            if (segment == null || string.IsNullOrWhiteSpace(segment.Text))
                return null;

            var label = segment.Text.Length > 20 ? segment.Text[..20] + "…" : segment.Text;
            var element = new TextElement
            {
                Name = label,
                Content = segment.Text,
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, CanvasHeight - 160),
                Width = 600,
                Height = 80,
                ZIndex = Elements.Count,
                SegmentId = segmentId
            };

            Elements.Add(element);
            _undoRedo?.Record(new ElementAddedAction(Elements, element));
            Log.Information("Auto-created TextElement for text segment {SegmentId}", segmentId);
            return element;
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

            RebuildSegmentSubscriptions();
            UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
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
                SyncPlaybackToVisibleTimelinePosition();
                _audioPlayerViewModel?.TogglePlayPauseCommand.Execute(null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error toggling play/pause");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Toggle loop playback on/off. Synced to TimelineViewModel.IsLoopPlayback.
        /// </summary>
        [RelayCommand]
        public void ToggleLoopPlayback()
        {
            IsLoopPlayback = !IsLoopPlayback;
            if (_timelineViewModel != null)
                _timelineViewModel.IsLoopPlayback = IsLoopPlayback;
        }

        /// <summary>
        /// Play audio from preview (kept for backward compatibility).
        /// Prefer TogglePlayPauseCommand for new code.
        /// </summary>
        [RelayCommand]
        public void Play()
        {
            SyncPlaybackToVisibleTimelinePosition();
            _audioPlayerViewModel?.PlayCommand.Execute(null);
            StatusMessage = "▶ Playing...";
        }

        /// <summary>
        /// Ensure the audio engine is aligned to the playhead the user currently sees before starting playback.
        /// This makes play resilient if a drag preview updated the visible playhead but the underlying audio
        /// position did not get committed yet due to input edge cases.
        /// </summary>
        private void SyncPlaybackToVisibleTimelinePosition()
        {
            if (_audioPlayerViewModel == null || _timelineViewModel == null)
                return;

            if (_audioPlayerViewModel.IsPlaying)
                return;

            double visiblePlayhead = _timelineViewModel.PlayheadPosition;
            if (double.IsNaN(visiblePlayhead) || double.IsInfinity(visiblePlayhead))
                return;

            if (Math.Abs(_audioPlayerViewModel.CurrentPosition - visiblePlayhead) < 0.01)
                return;

            _timelineViewModel.SeekTo(visiblePlayhead);
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

            if (_timelineViewModel != null)
                UpdateActivePreview(_timelineViewModel.PlayheadPosition);
            else
                SetActiveVisualLayout(ImageLayoutPresets.FullFrame);
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
                {
                    _timelineViewModel.ScriptApplied -= OnScriptApplied;
                    _timelineViewModel.ScrubCompleted -= OnScrubCompleted;
                }
                if (_projectViewModel != null && _projectPropertyChangedHandler != null)
                    _projectViewModel.PropertyChanged -= _projectPropertyChangedHandler;
                DetachTrackSubscriptions();

                // Stop debounce timer
                _previewDebounceTimer?.Stop();

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
        public async Task SaveElementsAsync(IProjectService projectService)
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
