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
        private readonly DispatcherTimer _elementSaveDebounceTimer;
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

        // Track-level text style propagation (CapCut model)
        private readonly TrackStylePropagationService _trackStylePropagation = new();

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

        /// <summary>Wire undo/redo. Called from MainViewModel after construction.</summary>
        public void SetUndoRedoService(UndoRedoService service)
        {
            _undoRedo = service;
            PropertyEditor.SetUndoRedoService(service);
        }

        /// <summary>Expose so CanvasView can record element-move actions.</summary>
        public UndoRedoService? UndoRedoService => _undoRedo;

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

            // Compute element rect by fitting the source image into the canvas while
            // preserving its native aspect ratio (prevents crop on non-matching ratios).
            var ownerTrack = FindTrackForSegment(segmentId);
            var (elemX, elemY, elemW, elemH) = ComputeFitRect(asset.FilePath, CanvasWidth, CanvasHeight);
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
        /// <summary>
        /// Compute an element rect that fits the source image inside the canvas while
        /// preserving the image's native aspect ratio. Falls back to full-canvas when
        /// the image cannot be read.
        /// </summary>
        private static (double X, double Y, double W, double H) ComputeFitRect(
            string imagePath, double canvasW, double canvasH)
        {
            double imgW = canvasW, imgH = canvasH;
            try
            {
                using var stream = new System.IO.FileStream(imagePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count > 0)
                {
                    imgW = decoder.Frames[0].PixelWidth;
                    imgH = decoder.Frames[0].PixelHeight;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComputeFitRect: failed to read image dimensions from {Path}, using full canvas", imagePath);
                return (0, 0, canvasW, canvasH);
            }

            double imgAspect = imgW / Math.Max(imgH, 1);
            double canvasAspect = canvasW / Math.Max(canvasH, 1);

            double elemW, elemH;
            if (imgAspect >= canvasAspect)
            {
                // Image is wider than canvas → fit to width
                elemW = canvasW;
                elemH = canvasW / imgAspect;
            }
            else
            {
                // Image is taller than canvas → fit to height
                elemH = canvasH;
                elemW = canvasH * imgAspect;
            }

            double elemX = (canvasW - elemW) / 2;
            double elemY = (canvasH - elemH) / 2;
            return (elemX, elemY, elemW, elemH);
        }

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

            // Apply track's shared text style template if available
            if (ownerTrack != null)
            {
                var template = ownerTrack.TextStyle;
                if (template != null)
                {
                    template.ApplyTo(element);
                    // Restore per-element properties that ApplyTo overwrites
                    element.Content = segment.Text;
                    element.Name = label;
                    element.SegmentId = segmentId;
                    element.ZIndex = ComputeZIndexForTrack(ownerTrack);
                }
                else
                {
                    // Initialize track template from this first element
                    _trackStylePropagation.InitializeTrackTemplate(ownerTrack, element);
                }
            }

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

        /// <summary>
        /// Ensure every text-track segment has a corresponding TextOverlayElement.
        /// Must be called before render snapshot so that lazily-created elements
        /// are present in the Elements collection for cloning.
        /// </summary>
        public void MaterializeAllTextElements()
        {
            if (_timelineViewModel == null) return;

            foreach (var track in _timelineViewModel.Tracks)
            {
                if (!string.Equals(track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var seg in track.Segments)
                {
                    if (string.IsNullOrWhiteSpace(seg.Id)) continue;
                    GetOrCreateTextElement(seg.Id);
                }
            }
        }

        // ─── Track-level text style propagation helpers ───────────────────

        /// <summary>
        /// Exposes the propagation service for use by CanvasView (drag/resize) and PropertyEditor.
        /// </summary>
        public TrackStylePropagationService TrackStylePropagation => _trackStylePropagation;

        /// <summary>
        /// Find the Track that owns a given TextOverlayElement (via its SegmentId).
        /// Returns null if the element has no SegmentId or the owning track is not a text track.
        /// </summary>
        public Track? FindOwnerTrack(TextOverlayElement element)
        {
            if (string.IsNullOrWhiteSpace(element.SegmentId) || _timelineViewModel == null)
                return null;

            foreach (var track in _timelineViewModel.Tracks)
            {
                if (!string.Equals(track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (track.Segments.Any(s => string.Equals(s.Id, element.SegmentId, StringComparison.Ordinal)))
                    return track;
            }
            return null;
        }

        /// <summary>
        /// Get all TextOverlayElements that belong to the same track as the given element.
        /// Includes the source element itself.
        /// </summary>
        public List<TextOverlayElement> GetTrackSiblingTextElements(Track track)
        {
            var segmentIds = new HashSet<string>(
                track.Segments.Select(s => s.Id),
                StringComparer.Ordinal);

            return Elements
                .OfType<TextOverlayElement>()
                .Where(e => e.SegmentId != null && segmentIds.Contains(e.SegmentId))
                .ToList();
        }

        /// <summary>
        /// Propagate position changes from a dragged/resized TextOverlayElement to all siblings on the same track.
        /// Called during canvas drag (real-time) for smooth visual feedback.
        /// </summary>
        public void PropagateTextElementPosition(TextOverlayElement source)
        {
            var track = FindOwnerTrack(source);
            if (track == null) return;

            var siblings = GetTrackSiblingTextElements(track);
            _trackStylePropagation.PropagatePositionFromElement(source, siblings);
        }

        /// <summary>
        /// Propagate all style changes from a TextOverlayElement to all siblings on the same track.
        /// Updates the Track.TextStyleJson template. Called after drag/resize completes and on property edits.
        /// </summary>
        public void PropagateTextElementStyle(TextOverlayElement source)
        {
            var track = FindOwnerTrack(source);
            if (track == null) return;

            var siblings = GetTrackSiblingTextElements(track);
            _trackStylePropagation.PropagateStyleFromElement(source, track, siblings);
        }

        /// <summary>
        /// Propagate a single named property change to track siblings.
        /// Returns true if propagation occurred.
        /// </summary>
        public bool PropagateTextElementProperty(TextOverlayElement source, string propertyName)
        {
            var track = FindOwnerTrack(source);
            if (track == null) return false;

            var siblings = GetTrackSiblingTextElements(track);
            return _trackStylePropagation.PropagateSingleProperty(source, propertyName, track, siblings);
        }

        public CanvasViewModel(VisualizerViewModel visualizerViewModel)
        {
            _visualizerViewModel = visualizerViewModel ?? throw new ArgumentNullException(nameof(visualizerViewModel));
            _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30fps
            };
            _visualizerTimer.Tick += OnVisualizerTimerTick;
            _elementSaveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _elementSaveDebounceTimer.Tick += OnElementSaveDebounceTick;
            PropertyEditor = new PropertyEditorViewModel();
            PropertyEditor.OnVisualizerElementConfigChanged = SyncVisualizerFromElement;
            PropertyEditor.OnTextElementPropertyChanged = OnTextElementPropertyChanged;
            PropertyEditor.OnElementPropertyEdited = OnElementPropertyEdited;
            ApplyAspectRatio(selectedAspectRatio);
        }

        private void OnElementPropertyEdited(CanvasElement element, string propertyName)
        {
            if (_disposed || _projectViewModel?.CurrentProject == null)
                return;

            _elementSaveDebounceTimer.Stop();
            _elementSaveDebounceTimer.Start();
        }

        private async void OnElementSaveDebounceTick(object? sender, EventArgs e)
        {
            _elementSaveDebounceTimer.Stop();

            if (_disposed || _projectViewModel?.CurrentProject == null)
                return;

            try
            {
                await _projectViewModel.SaveProjectAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist debounced element property changes");
            }
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
            _visualizerViewModel.PrimaryColorHex = element.PrimaryColorHex;
            _visualizerViewModel.CustomGradientColors = element.CustomGradientColors;
            _visualizerViewModel.BarGradientDarkness = element.BarGradientDarkness;
            _visualizerViewModel.BarGradientEnabled = element.BarGradientEnabled;
            _visualizerViewModel.BarGradientBaseColorHex = element.BarGradientBaseColorHex;
        }

        private void SyncVisualizerStateFromLoadedElements(IEnumerable<CanvasElement> loadedElements)
        {
            if (_visualizerViewModel == null)
                return;

            var visualizer = loadedElements.OfType<VisualizerElement>().FirstOrDefault();
            if (visualizer != null)
            {
                SyncVisualizerFromElement(visualizer);
                return;
            }

            // Reset shared visualizer config when the loaded project has no visualizer elements.
            // Without this, a palette/style from the previous project can leak into the next one.
            SyncVisualizerFromElement(new VisualizerElement());
        }

        /// <summary>
        /// Called when a TextOverlayElement property is changed via PropertyEditor.
        /// Captures sibling old values, propagates the change, and returns old values for undo support.
        /// </summary>
        private List<(CanvasElement Element, object? OldValue)>? OnTextElementPropertyChanged(TextOverlayElement element, string propertyName)
        {
            // Reverse sync: keep Segment.Text in sync when Content is edited via property editor
            if (string.Equals(propertyName, nameof(TextOverlayElement.Content), StringComparison.Ordinal))
            {
                var seg = FindSegmentById(element.SegmentId);
                if (seg != null && !string.Equals(seg.Text, element.Content, StringComparison.Ordinal))
                {
                    seg.Text = element.Content;
                }
            }

            var track = FindOwnerTrack(element);
            if (track == null) return null;

            if (TrackStylePropagationService.IsExcludedProperty(propertyName))
                return null;

            var prop = typeof(TextOverlayElement).GetProperty(propertyName)
                       ?? typeof(CanvasElement).GetProperty(propertyName);
            if (prop == null) return null;

            var siblings = GetTrackSiblingTextElements(track);
            var siblingOldValues = new List<(CanvasElement, object?)>();

            // Capture old values BEFORE propagation
            foreach (var sibling in siblings)
            {
                if (ReferenceEquals(sibling, element)) continue;
                siblingOldValues.Add((sibling, prop.GetValue(sibling)));
            }

            // Now propagate
            _trackStylePropagation.PropagateSingleProperty(element, propertyName, track, siblings);

            return siblingOldValues.Count > 0 ? siblingOldValues : null;
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
        /// Add a text element with the given style preset to canvas.
        /// When <paramref name="startTime"/> is provided the segment is placed at that
        /// position (drag-drop); otherwise it uses the playhead (button-click fallback).
        /// </summary>
        public void AddTextElementWithPreset(TextStyle preset, double? startTime = null, string? trackId = null)
        {
            bool isTitle = preset == TextStyle.Title;
            var element = new TextOverlayElement
            {
                Name = $"{preset} {Elements.Count + 1}",
                X = Math.Max(0, (CanvasWidth - (isTitle ? 400 : 600)) / 2),
                Y = isTitle ? Math.Max(0, CanvasHeight * 0.08) : Math.Max(0, CanvasHeight - 160),
                Width = isTitle ? 400 : 600,
                Height = isTitle ? 100 : 80
            };
            element.ApplyPreset(preset);

            if (startTime.HasValue)
                AddElementAtTime(element, TrackTypes.Text, startTime.Value, 5.0, trackId);
            else
                AddElementToCanvas(element, TrackTypes.Text);
        }

        /// <summary>
        /// Add a new title element to canvas (button-click fallback at playhead).
        /// </summary>
        [RelayCommand]
        public void AddTitleElement() => AddTextElementWithPreset(TextStyle.Title);

        /// <summary>
        /// Add a new text element to canvas (button-click fallback at playhead).
        /// </summary>
        [RelayCommand]
        public void AddTextElement() => AddTextElementWithPreset(TextStyle.Caption);

        /// <summary>
        /// Add a new visualizer element to canvas.
        /// When <paramref name="startTime"/> is provided the segment is placed at that
        /// position (drag-drop); otherwise it uses the playhead.
        /// </summary>
        public void AddVisualizerElementAt(double? startTime = null, string? trackId = null)
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
            var playhead         = startTime ?? _timelineViewModel?.PlayheadPosition ?? 0.0;
            var segmentDuration  = Math.Max(10.0, projectEnd - playhead);

            if (startTime.HasValue)
                AddElementAtTime(element, TrackTypes.Effect, startTime.Value, segmentDuration, trackId);
            else
                AddElementToCanvas(element, TrackTypes.Effect, segmentDuration);

            EnsureVisualizerTimer();
        }

        /// <summary>
        /// Add a new visualizer element to canvas (button-click fallback at playhead).
        /// </summary>
        [RelayCommand]
        public void AddVisualizerElement() => AddVisualizerElementAt();

        /// <summary>
        /// Handle an asset (image/video) dropped onto the preview canvas.
        /// Creates a full-span overlay segment (0 → project end) on a new track
        /// and an ImageElement at the drop position. Used for logos, watermarks,
        /// and other persistent overlay elements.
        /// </summary>
        public bool AddOverlayFromAssetDrop(Asset asset, double canvasX, double canvasY)
        {
            if (asset == null || _timelineViewModel == null)
                return false;

            var assetType = asset.Type?.ToLowerInvariant() ?? "";
            if (assetType is not ("image" or "video"))
                return false;

            _isCreatingElement = true;
            try
            {
                // Create full-span segment on a new visual track
                Segment? segment = _timelineViewModel.CreateFullSpanOverlaySegment(
                    TrackTypes.Visual, asset.Name ?? "Overlay", asset.Id);

                if (segment == null)
                    return false;

                // Compute element rect from the source image, then offset to the drop position
                var (elemX, elemY, elemW, elemH) = ComputeFitRect(asset.FilePath, CanvasWidth, CanvasHeight);

                // If the asset is small (e.g., a logo), position at the drop point;
                // if it's full-frame, use the computed fit position.
                bool isSmallOverlay = elemW < CanvasWidth * 0.5 && elemH < CanvasHeight * 0.5;
                double finalX = isSmallOverlay ? Math.Max(0, Math.Min(canvasX, CanvasWidth - elemW)) : elemX;
                double finalY = isSmallOverlay ? Math.Max(0, Math.Min(canvasY, CanvasHeight - elemH)) : elemY;

                var element = new ImageElement
                {
                    Name = asset.Name ?? Path.GetFileNameWithoutExtension(asset.FilePath) ?? "Overlay",
                    FilePath = asset.FilePath,
                    X = finalX,
                    Y = finalY,
                    Width = elemW,
                    Height = elemH,
                    ZIndex = ComputeZIndexForTrack(FindTrackForSegment(segment.Id)),
                    SegmentId = segment.Id
                };

                Elements.Add(element);
                SelectElement(element);

                var segAction = _undoRedo?.PopLast();
                var elAction = new ElementAddedAction(Elements, element);
                if (segAction != null)
                    _undoRedo?.Record(new CompoundAction($"Add overlay {element.Name}", new[] { segAction, elAction }));
                else
                    _undoRedo?.Record(elAction);

                LogMessage($"Added full-span overlay '{element.Name}'");
                return true;
            }
            finally
            {
                _isCreatingElement = false;
            }
        }

        /// <summary>
        /// Add an element at a specific time position on the timeline (for drag-drop).
        /// Creates a segment at the given startTime instead of the playhead position.
        /// </summary>
        private Segment? AddElementAtTime(CanvasElement element, string trackType, double startTime, double duration, string? trackId = null)
        {
            _isCreatingElement = true;
            try
            {
                if (_timelineViewModel == null)
                {
                    LogMessage("Cannot add element: no project loaded");
                    return null;
                }

                Segment? segment = _timelineViewModel.CreateSegmentForElementAtTime(
                    trackType, element.Name, startTime, duration, trackId);

                if (segment == null)
                    return null;

                element.SegmentId = segment.Id;
                element.ZIndex = ComputeZIndexForTrack(FindTrackForSegment(segment.Id));

                Elements.Add(element);
                SelectElement(element);

                var segAction = _undoRedo?.PopLast();
                var elAction = new ElementAddedAction(Elements, element);
                if (segAction != null)
                    _undoRedo?.Record(new CompoundAction($"Add {element.Type}", new[] { segAction, elAction }));
                else
                    _undoRedo?.Record(elAction);

                LogMessage($"Added {element.Type} element at {startTime:F2}s");
                return segment;
            }
            finally
            {
                _isCreatingElement = false;
            }
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
            // NOTE: LoadTracksFromProject() triggers OnTracksCollectionChanged → UpdateActivePreview
            //       → EnsureTextElementForSegment which may have already created some elements.
            //       Use GetOrCreateTextElement to avoid duplicates.
            int created = 0;
            foreach (var segment in e.NewSegments)
            {
                if (string.IsNullOrWhiteSpace(segment.Id)) continue;

                // Skip if element already exists (created by UpdateActivePreview during LoadTracksFromProject)
                bool alreadyExists = Elements.Any(el =>
                    string.Equals(el.SegmentId, segment.Id, StringComparison.Ordinal));
                if (alreadyExists) continue;

                GetOrCreateTextElement(segment.Id);
                created++;
            }

            LogMessage($"Canvas: created {created} text element(s) from script");
            Log.Information("Canvas elements synced after script apply: {Created} new, {Removed} removed, {Skipped} already existed",
                created, orphaned.Count, e.NewSegments.Count - created);

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

                // Wire track style info badge for text elements on shared-style tracks
                if (element is TextOverlayElement textEl)
                {
                    var track = FindOwnerTrack(textEl);
                    var segCount = track?.Segments.Count ?? 0;
                    PropertyEditor.TrackStyleInfoText = track != null && segCount > 1
                        ? $"🔗 Changes apply to all {segCount} text segments on \"{track.Name}\""
                        : null;
                }
                else
                {
                    PropertyEditor.TrackStyleInfoText = null;
                }

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
        /// Flip the selected element horizontally.
        /// </summary>
        [RelayCommand]
        public void FlipHorizontal()
        {
            if (SelectedElement == null) return;
            var oldVal = SelectedElement.FlipH;
            SelectedElement.FlipH = !oldVal;
            _undoRedo?.Record(new ElementPropertyChangedAction(
                SelectedElement,
                typeof(CanvasElement).GetProperty(nameof(CanvasElement.FlipH))!,
                oldVal, SelectedElement.FlipH));
            LogMessage($"Flip H: {SelectedElement.Name}");
        }

        /// <summary>
        /// Flip the selected element vertically.
        /// </summary>
        [RelayCommand]
        public void FlipVertical()
        {
            if (SelectedElement == null) return;
            var oldVal = SelectedElement.FlipV;
            SelectedElement.FlipV = !oldVal;
            _undoRedo?.Record(new ElementPropertyChangedAction(
                SelectedElement,
                typeof(CanvasElement).GetProperty(nameof(CanvasElement.FlipV))!,
                oldVal, SelectedElement.FlipV));
            LogMessage($"Flip V: {SelectedElement.Name}");
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
                _timelineViewModel?.ResetScrubState();
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
            _timelineViewModel?.ResetScrubState();
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
                    _timelineViewModel.TrackScaleModeApplied -= OnTrackScaleModeApplied;
                }
                if (_projectViewModel != null && _projectPropertyChangedHandler != null)
                    _projectViewModel.PropertyChanged -= _projectPropertyChangedHandler;
                DetachTrackSubscriptions();

                if (_selectionSyncService != null)
                    _selectionSyncService.TimelineSelectionChanged -= OnTimelineSegmentSelected;

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
                _elementSaveDebounceTimer.Stop();
                _elementSaveDebounceTimer.Tick -= OnElementSaveDebounceTick;
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
                // Remove canvas elements referencing deleted segments before saving.
                // Without this, elements with stale SegmentId cause FK constraint failures
                // because the segment was already deleted from DB by UpdateProjectAsync.
                ValidateElementSegmentIds();

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
                    SyncVisualizerStateFromLoadedElements(Array.Empty<CanvasElement>());
                    // Recreate auto-generated elements for active segments
                    UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
                    return;
                }

                var canvasElements = ElementMapper.ToCanvasElements(projectElements);
                Elements.Clear();
                foreach (var ce in canvasElements)
                    Elements.Add(ce);

                var imgCount = canvasElements.Count(e => e is ImageElement);
                var txtCount = canvasElements.Count(e => e is TextOverlayElement);
                Log.Information("LoadElementsFromProject: loaded {Total} elements ({Img} image, {Txt} text) from DB",
                    canvasElements.Count, imgCount, txtCount);

                // Validate SegmentIds: nullify references to segments that no longer exist
                ValidateElementSegmentIds();

                SyncVisualizerStateFromLoadedElements(Elements);

                SelectedElement = null;
                PropertyEditor.SetSelectedElement(null);
                EnsureVisualizerTimer();

                // Re-run preview to recreate auto-generated elements (ImageElements for
                // visual segments, TextOverlayElements for text segments) that were not
                // persisted. Without this, Elements.Clear() above wipes runtime-only
                // elements that the preview pipeline created before LoadElementsFromProject ran.
                UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);

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
