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
        private ProjectViewModel? _projectViewModel;
        private TimelineViewModel? _timelineViewModel;
        private SelectionSyncService? _selectionSyncService;
        private UndoRedoService? _undoRedo;
        private ObservableCollection<Track>? _trackedTracks;
        private HashSet<Segment>? _trackedSegments;
        private NotifyCollectionChangedEventHandler? _tracksCollectionChangedHandler;
        private readonly Dictionary<string, NotifyCollectionChangedEventHandler> _segmentsCollectionHandlers = new(StringComparer.Ordinal);
        // Cache orphaned elements by SegmentId so Undo (segment re-insert) can restore them
        private readonly Dictionary<string, List<CanvasElement>> _orphanedElementCache = new(StringComparer.Ordinal);
        private PropertyChangedEventHandler? _trackPropertyChangedHandler;
        private PropertyChangedEventHandler? _segmentPropertyChangedHandler;
        private PropertyChangedEventHandler? _timelinePropertyChangedHandler;
        private PropertyChangedEventHandler? _projectPropertyChangedHandler;
        private PropertyChangedEventHandler? _audioPlayerPropertyChangedHandler;
        private bool _disposed;
        // Guard: prevents selection-sync auto-creation while an AddElement method is in progress
        private bool _isCreatingElement;

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
        private Uri? videoSource;

        [ObservableProperty]
        private TimeSpan videoPosition;

        [ObservableProperty]
        private bool isVideoMode;

        [ObservableProperty]
        private bool isVisualPlaceholderVisible = true;

        [ObservableProperty]
        private Segment? activeVisualSegment;

        /// <summary>
        /// Canvas.ZIndex for the video MediaElement so it participates in the unified compositing
        /// z-order alongside ImageElements, VisualizerElements, etc. Computed from Track.Order.
        /// </summary>
        [ObservableProperty]
        private int primaryVideoZIndex;

        [ObservableProperty]
        private double activeVisualX;

        [ObservableProperty]
        private double activeVisualY;

        [ObservableProperty]
        private double activeVisualWidth = 1920;

        [ObservableProperty]
        private double activeVisualHeight = 1080;

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
        public void SetUndoRedoService(UndoRedoService service)
        {
            _undoRedo = service;
            PropertyEditor.SetUndoRedoService(service);
        }

        /// <summary>Expose so CanvasView can record element-move actions.</summary>
        public UndoRedoService? UndoRedoService => _undoRedo;

        private void OnTimelineSegmentSelected(string? segmentId, bool playheadInRange)
        {
            if (string.IsNullOrEmpty(segmentId))
            {
                SelectElement(null);
                return;
            }

            // Skip auto-creation when an Add*Element method is already in progress.
            // The calling method will add the element itself after segment creation.
            if (_isCreatingElement)
                return;

            // Highlight linked canvas element regardless of playhead position.
            var linked = Elements.FirstOrDefault(e => string.Equals(e.SegmentId, segmentId, StringComparison.Ordinal));

            // If no linked element exists, auto-create one for image or text segments
            if (linked == null)
                linked = (CanvasElement?)TryCreateImageElementForSegment(segmentId)
                      ?? GetOrCreateTextElement(segmentId);

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

            // Create an ImageElement using the track's ImageLayoutPreset so the auto-created
            // element matches the preview (Square_Center, Widescreen_Center, or FullFrame).
            var ownerTrack = FindTrackForSegment(segmentId);
            var layoutPreset = ownerTrack?.ImageLayoutPreset ?? global::PodcastVideoEditor.Core.Models.ImageLayoutPresets.FullFrame;
            var (elemX, elemY, elemW, elemH) = global::PodcastVideoEditor.Core.RenderHelper.ComputeImageRect(layoutPreset, CanvasWidth, CanvasHeight);
            var element = new ImageElement
            {
                Name = asset.Name ?? Path.GetFileNameWithoutExtension(asset.FilePath) ?? "Image",
                FilePath = asset.FilePath,
                X = elemX,
                Y = elemY,
                Width = elemW,
                Height = elemH,
                ZIndex = ComputeZIndexForTrack(ownerTrack),
                SegmentId = segmentId
            };

            Elements.Add(element);
            Log.Information("Auto-created ImageElement for visual segment {SegmentId}", segmentId);
            return element;
        }

        /// <summary>
        /// Compute a Canvas.ZIndex value for an element based on its parent track's Order.
        /// Lower Track.Order (foreground) → higher ZIndex (drawn on top in WPF Canvas).
        /// Falls back to <see cref="Elements.Count"/> when track info is unavailable.
        /// </summary>
        private int ComputeZIndexForTrack(Track? track)
        {
            if (track == null || _timelineViewModel == null)
                return Elements.Count;

            // maxOrder is the highest Order value across all tracks.
            // ZIndex = maxOrder - track.Order so that Order 0 gets the highest ZIndex.
            var maxOrder = _timelineViewModel.Tracks.Count > 0
                ? _timelineViewModel.Tracks.Max(t => t.Order)
                : 0;
            return maxOrder - track.Order;
        }

        /// <summary>
        /// Recalculate ZIndex for every canvas element based on current track Order values.
        /// Called when tracks are reordered so the visual stacking stays in sync.
        /// </summary>
        private void RefreshElementZIndices()
        {
            foreach (var element in Elements)
            {
                var track = FindTrackForSegment(element.SegmentId);
                element.ZIndex = ComputeZIndexForTrack(track);
            }
        }

        /// <summary>
        /// Find the track that owns the given segment.
        /// </summary>
        private Track? FindTrackForSegment(string? segmentId)
        {
            if (string.IsNullOrEmpty(segmentId) || _timelineViewModel == null)
                return null;

            foreach (var track in _timelineViewModel.Tracks)
            {
                if (track.Segments?.Any(s => string.Equals(s.Id, segmentId, StringComparison.Ordinal)) == true)
                    return track;
            }
            return null;
        }

        /// <summary>
        /// Find a segment by its ID across all tracks.
        /// </summary>
        private Segment? FindSegmentById(string? segmentId)
        {
            if (string.IsNullOrEmpty(segmentId) || _timelineViewModel == null)
                return null;

            foreach (var track in _timelineViewModel.Tracks)
            {
                var seg = track.Segments?.FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.Ordinal));
                if (seg != null)
                    return seg;
            }
            return null;
        }

        /// <summary>
        /// Returns an existing TextOverlayElement for the given segment, or creates one if none exists.
        /// Used both by preview pipeline and timeline-segment-selection to ensure a single
        /// code path for text element creation, eliminating duplication.
        /// </summary>
        private TextOverlayElement? GetOrCreateTextElement(string segmentId)
        {
            if (string.IsNullOrWhiteSpace(segmentId))
                return null;

            // Fast lookup: existing element?
            var existing = Elements.FirstOrDefault(e =>
                string.Equals(e.SegmentId, segmentId, StringComparison.Ordinal));
            if (existing is TextOverlayElement tov)
                return tov;
            if (existing != null)
                return null; // Non-text element linked — don't create a text overlay

            // Find the segment and verify it's on a text track
            Segment? segment = null;
            Track? ownerTrack = null;
            if (_timelineViewModel != null)
            {
                foreach (var track in _timelineViewModel.Tracks)
                {
                    if (!string.Equals(track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var seg = track.Segments.FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.Ordinal));
                    if (seg != null)
                    {
                        segment = seg;
                        ownerTrack = track;
                        break;
                    }
                }
            }

            if (segment == null || string.IsNullOrWhiteSpace(segment.Text))
                return null;

            var label = segment.Text.Length > 20 ? segment.Text[..20] + "…" : segment.Text;
            var element = new TextOverlayElement
            {
                Name = label,
                Content = segment.Text,
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, CanvasHeight - 160),
                Width = 600,
                Height = 80,
                ZIndex = ComputeZIndexForTrack(ownerTrack),
                SegmentId = segmentId
            };

            Elements.Add(element);
            Log.Information("Auto-created TextOverlayElement for text segment {SegmentId}", segmentId);
            return element;
        }

        /// <summary>
        /// Called from UpdateActivePreview: proactively creates an interactive TextOverlayElement
        /// for a segment that has no linked canvas element yet.
        /// </summary>
        private void EnsureTextElementForSegment(Segment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.Id))
                return;
            GetOrCreateTextElement(segment.Id);
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
            _visualizerViewModel.SmoothingFactor = element.SmoothingFactor;
            _visualizerViewModel.ShowPeaks = element.ShowPeaks;
            _visualizerViewModel.SymmetricMode = element.SymmetricMode;
            _visualizerViewModel.PeakHoldTime = element.PeakHoldTime;
            _visualizerViewModel.BarWidth = element.BarWidth;
            _visualizerViewModel.BarSpacing = element.BarSpacing;
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
                _visualizerViewModel.StopRendering();
            }
        }

        /// <summary>
        /// Stop visualizer timer and rendering. Called when canvas is unloaded (tab switch)
        /// so that <see cref="EnsureVisualizerTimer"/> can reinitialize on next load.
        /// </summary>
        public void StopVisualizerTimer()
        {
            if (_visualizerTimer != null && _visualizerTimer.IsEnabled)
                _visualizerTimer.Stop();

            _visualizerViewModel?.StopRendering();
        }

        /// <summary>
        /// Called when the canvas view is (re-)loaded (e.g. returning to Edit tab).
        /// Restarts the visualizer and refreshes waveform peaks for audio segments.
        /// </summary>
        public void OnCanvasReloaded()
        {
            EnsureVisualizerTimer();
            _timelineViewModel?.RefreshWaveformPeaks();
        }

        /// <summary>
        /// Shared logic for all Add*Element commands. Creates the timeline segment (with
        /// selection-sync suppressed to avoid auto-creating a duplicate element), links
        /// the element, adds it to the canvas, selects it, and records an undo action.
        /// </summary>
        private Segment? AddElementToCanvas(CanvasElement element, string trackType, double? duration = null, bool newTrack = false)
        {
            _isCreatingElement = true;
            try
            {
                if (_timelineViewModel == null)
                {
                    LogMessage("Cannot add element: no project loaded");
                    return null;
                }

                Segment? segment = newTrack
                    ? _timelineViewModel.CreateSegmentOnNewTrack(trackType, element.Name, duration ?? 5.0)
                    : _timelineViewModel.CreateSegmentForElement(trackType, element.Name, duration ?? 5.0);

                if (segment == null)
                    return null; // Segment creation failed (collision or no active project)

                element.SegmentId = segment.Id;
                element.ZIndex = ComputeZIndexForTrack(FindTrackForSegment(segment.Id));

                Elements.Add(element);
                SelectElement(element);

                // Compound undo: pop the SegmentAddedAction that CommitSegmentToTrack just recorded,
                // then wrap it with ElementAddedAction so both are undone/redone atomically.
                var segAction = _undoRedo?.PopLast();
                var elAction = new ElementAddedAction(Elements, element);
                if (segAction != null)
                    _undoRedo?.Record(new CompoundAction($"Add {element.Type}", new[] { segAction, elAction }));
                else
                    _undoRedo?.Record(elAction);

                LogMessage($"Added {element.Type} element + timeline segment");
                return segment;
            }
            finally
            {
                _isCreatingElement = false;
            }
        }

        /// <summary>
        /// Add a new title element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTitleElement()
        {
            var element = new TextOverlayElement
            {
                Name = $"Title {Elements.Count + 1}",
                X = Math.Max(0, (CanvasWidth - 400) / 2),
                Y = Math.Max(0, CanvasHeight * 0.08),
                Width = 400,
                Height = 100
            };
            element.ApplyPreset(TextStyle.Title);
            AddElementToCanvas(element, TrackTypes.Text);
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
                Height = 200
            };
            AddElementToCanvas(element, TrackTypes.Visual);
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
                Height = 400
            };

            // Use the full project duration so the baked visualizer covers the whole
            // project, not just the 5-second default stub.
            var projectEnd       = _timelineViewModel?.GetProjectDuration() ?? 30.0;
            var playhead         = _timelineViewModel?.PlayheadPosition ?? 0.0;
            var segmentDuration  = Math.Max(10.0, projectEnd - playhead);
            AddElementToCanvas(element, TrackTypes.Effect, segmentDuration);
            EnsureVisualizerTimer();
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
                Height = 300
            };
            AddElementToCanvas(element, TrackTypes.Visual);
        }

        /// <summary>
        /// Add a new text element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTextElement()
        {
            var element = new TextOverlayElement
            {
                Name = $"Text {Elements.Count + 1}",
                X = Math.Max(0, (CanvasWidth - 600) / 2),
                Y = Math.Max(0, CanvasHeight - 160),
                Width = 600,
                Height = 80
            };
            AddElementToCanvas(element, TrackTypes.Text);
        }

        /// <summary>
        /// Remove canvas elements whose SegmentId matches one of the deleted segment IDs.
        /// Returns the list of removed elements (useful for undo scenarios).
        /// </summary>
        internal List<CanvasElement> RemoveOrphanedElements(IEnumerable<string> deletedSegmentIds)
        {
            var deletedSet = deletedSegmentIds as ISet<string>
                ?? new HashSet<string>(deletedSegmentIds, StringComparer.Ordinal);

            var orphaned = Elements
                .Where(el => el.SegmentId != null && deletedSet.Contains(el.SegmentId))
                .ToList();

            foreach (var old in orphaned)
                Elements.Remove(old);

            if (orphaned.Count > 0)
            {
                EnsureVisualizerTimer();
                Log.Information("Removed {Count} orphaned canvas element(s) for deleted segment(s)", orphaned.Count);
            }

            return orphaned;
        }

        /// <summary>
        /// Called when ApplyScriptAsync completes. Removes canvas elements that were linked to
        /// the replaced text-track segments, then creates a new TextElement for every new segment
        /// so script text is editable/draggable instead of being a read-only subtitle overlay.
        /// </summary>
        private void OnScriptApplied(object? sender, ScriptAppliedEventArgs e)
        {
            // Remove canvas elements that belonged to the now-replaced text-track segments
            var orphaned = RemoveOrphanedElements(e.ReplacedSegmentIds);

            // Create an interactive TextElement for each new segment, positioned at subtitle area (center-bottom)
            double subtitleX = Math.Max(0, (CanvasWidth - 600) / 2);
            double subtitleY = Math.Max(0, CanvasHeight - 160);
            foreach (var segment in e.NewSegments)
            {
                var label = segment.Text.Length > 20 ? segment.Text[..20] + "…" : segment.Text;
                var element = new TextOverlayElement
                {
                    Name = label,
                    Content = segment.Text,
                    X = subtitleX,
                    Y = subtitleY,
                    Width = 600,
                    Height = 80,
                    ZIndex = ComputeZIndexForTrack(FindTrackForSegment(segment.Id)),
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
                // Wire timing badge: find bound segment for this element
                PropertyEditor.SetBoundSegment(FindSegmentById(element.SegmentId));
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
            var elAction = new ElementDeletedAction(Elements, elementToDelete);

            // Cascade-delete the linked timeline segment (if any) and record as compound undo
            IUndoableAction? segAction = null;
            if (!string.IsNullOrEmpty(elementToDelete.SegmentId) && _timelineViewModel != null)
                segAction = _timelineViewModel.RemoveSegmentById(elementToDelete.SegmentId);

            Elements.Remove(elementToDelete);
            SelectedElement = null;
            PropertyEditor.SetSelectedElement(null);
            EnsureVisualizerTimer();

            if (segAction != null)
                _undoRedo?.Record(new CompoundAction($"Delete {elementToDelete.Name}", new[] { elAction, segAction }));
            else
                _undoRedo?.Record(elAction);

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
            cloned.ZIndex = ComputeZIndexForTrack(FindTrackForSegment(cloned.SegmentId));

            Elements.Add(cloned);
            SelectElement(cloned);
            EnsureVisualizerTimer();
            _undoRedo?.Record(new ElementAddedAction(Elements, cloned));
            LogMessage($"Duplicated: {cloned.Name}");
        }

        /// <summary>
        /// Bring selected element to front by moving its track to the top (lowest Order).
        /// Uses track reorder so the z-order change is persistent and consistent with the
        /// track-based compositing model used by commercial editors.
        /// </summary>
        [RelayCommand]
        public void BringToFront()
        {
            if (SelectedElement == null || _timelineViewModel == null) return;

            var track = FindTrackForSegment(SelectedElement.SegmentId);
            if (track == null)
            {
                LogMessage("Cannot bring to front: element has no track");
                return;
            }

            _timelineViewModel.ReorderTrack(track, 0);
            RefreshElementZIndices();
            LogMessage($"Moved {SelectedElement.Name} to front (track reorder)");
        }

        /// <summary>
        /// Send selected element to back by moving its track to the bottom (highest Order).
        /// </summary>
        [RelayCommand]
        public void SendToBack()
        {
            if (SelectedElement == null || _timelineViewModel == null) return;

            var track = FindTrackForSegment(SelectedElement.SegmentId);
            if (track == null)
            {
                LogMessage("Cannot send to back: element has no track");
                return;
            }

            _timelineViewModel.ReorderTrack(track, _timelineViewModel.Tracks.Count - 1);
            RefreshElementZIndices();
            LogMessage($"Moved {SelectedElement.Name} to back (track reorder)");
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
        /// Only performs a seek if there is a SIGNIFICANT drift (>200ms), which can happen if a scrub preview
        /// updated the visible playhead but the audio position was never committed.
        /// Small drifts (&lt;200ms) are normal — they arise from dual-timer polling jitter between
        /// AudioPlayerViewModel and TimelineViewModel. The coordinator's OnPlaybackStarted handler
        /// already syncs position atomically on resume, so we must NOT re-seek for small deltas
        /// (doing so would cause an audible pop and visible needle jump).
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

            // Only seek for significant drift (uncommitted scrub).
            // Small drift from dual-timer jitter is handled by the coordinator on PlaybackStarted.
            if (Math.Abs(_audioPlayerViewModel.CurrentPosition - visiblePlayhead) < 0.2)
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
                if (_previewDebounceTimer != null)
                    _previewDebounceTimer.Tick -= OnPreviewDebounceTimerTick;

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
                _visualizerViewModel?.StopRendering();

                PropertyEditor?.Dispose();
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

                // Validate SegmentIds: nullify references to segments that no longer exist
                ValidateElementSegmentIds();

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

        /// <summary>
        /// Validate all element SegmentIds against current project segments.
        /// Elements referencing non-existent segments are removed — they were auto-generated
        /// to represent a segment that has since been deleted and have no standalone value.
            /// Elements with SegmentId=null are left untouched when they are intentional global overlays.
            /// Ghost text overlays left behind by older bugs are removed during the same pass.
        /// </summary>
        private void ValidateElementSegmentIds()
        {
            if (_timelineViewModel == null)
                return;

            var validSegmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in _timelineViewModel.Tracks)
            {
                foreach (var seg in track.Segments)
                {
                    if (seg.Id != null)
                        validSegmentIds.Add(seg.Id);
                }
            }

            var toRemove = new List<CanvasElement>();
            foreach (var element in Elements)
            {
                // Remove elements with stale SegmentId (segment was deleted)
                if (element.SegmentId != null && !validSegmentIds.Contains(element.SegmentId))
                {
                    Log.Warning("Element {ElementId} ({Name}) has stale SegmentId {SegmentId}, removing element",
                        element.Id, element.Name, element.SegmentId);
                    toRemove.Add(element);
                    continue;
                }

                // Remove ghost TextOverlayElements: orphaned global overlays with empty or
                // legacy placeholder content (leftover from previous default-value bug).
                if (element is TextOverlayElement tov
                    && element.SegmentId == null
                    && (string.IsNullOrWhiteSpace(tov.Content)
                        || string.Equals(tov.Content, "Text", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tov.Content, "Title", StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Warning("Removing ghost TextOverlayElement {ElementId} ({Name}) with placeholder Content '{Content}'",
                        element.Id, element.Name, tov.Content);
                    toRemove.Add(element);
                }
            }

            foreach (var element in toRemove)
                Elements.Remove(element);

            if (toRemove.Count > 0)
                Log.Information("Removed {Count} orphaned/ghost element(s) during validation", toRemove.Count);
        }

    }
}
