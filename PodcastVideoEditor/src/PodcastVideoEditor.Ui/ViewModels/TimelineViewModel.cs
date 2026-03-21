#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for timeline editing - manages segments and playhead sync.
    /// ST-9: Timeline Editor & Segment Manager
    /// </summary>
    public partial class TimelineViewModel : ObservableObject
    {
        private readonly AudioService _audioService;
        private readonly ProjectViewModel _projectViewModel;
        private SelectionSyncService? _selectionSyncService;
        private CancellationTokenSource? _playheadSyncCts;
        private bool _isPlayheadSyncing;
        private volatile bool _isScrubbing;
        private double _lastSyncedPlayhead = -1;

        [ObservableProperty]
        private ObservableCollection<Track> tracks = new();

        [ObservableProperty]
        private Track? selectedTrack;

        [ObservableProperty]
        private Segment? selectedSegment;

        [ObservableProperty]
        private double playheadPosition = 0.0; // in seconds

        [ObservableProperty]
        private double timelineWidth = 800.0; // pixels

        [ObservableProperty]
        private double totalDuration = 60.0; // seconds (from audio)

        [ObservableProperty]
        private double pixelsPerSecond = 10.0; // calculated

        [ObservableProperty]
        private double gridSize = 0.01; // 10ms grid snapping (2 decimal places, matches script [start → end])

        [ObservableProperty]
        private string statusMessage = "Timeline ready";

        /// <summary>
        /// Raw script text for paste (ST-3). Format: [start → end] text per line.
        /// </summary>
        [ObservableProperty]
        private string scriptPasteText = string.Empty;

        /// <summary>Waveform peak data for the primary audio track (legacy; per-segment peaks now in Segment.WaveformPeaks).</summary>
        [ObservableProperty]
        private float[] audioPeaks = Array.Empty<float>();


        /// <summary>
        /// When true, defer thumbnail strip updates (during resize/drag) to avoid FFmpeg blocking UI.
        /// </summary>
        [ObservableProperty]
        private bool isDeferringThumbnailUpdate;

        [ObservableProperty]
        private bool isPlaying = false;

        [ObservableProperty]
        private bool isLoopPlayback = false;

        [ObservableProperty]
        private string aIAnalysisStatus = string.Empty;

        private IAIAnalysisOrchestrator? _aiOrchestrator;
        private bool _isAnalyzing;

        private const int WaveformBinCount = 2400;
        private const int ActiveSyncIntervalMs = 33;
        private const int IdleSyncIntervalMs = 150;
        private const double PositionDeltaThreshold = 0.002;
        private const double DefaultSegmentDurationSeconds = 5.0;
        // Cache for GetActiveSegmentsAtTime: avoid duplicate allocations within the same dispatch frame.
        // CanvasViewModel and the audio playback loop both call this per frame.
        private double _cachedActiveSegmentsTime = double.MinValue;
        private List<(Track track, Segment segment)>? _cachedActiveSegments;

        // Undo/redo (optional — set via SetUndoRedoService from MainViewModel).
        private UndoRedoService? _undoRedo;

        /// <summary>Expose so TimelineView can record drag-complete timing changes.</summary>
        public UndoRedoService? UndoRedoService => _undoRedo;

        /// <summary>
        /// Wire up undo/redo. Called from MainViewModel after construction.
        /// </summary>
        public void SetUndoRedoService(UndoRedoService service) => _undoRedo = service;

        /// <summary>Select the given track (called from TimelineView code-behind).</summary>
        public void SelectTrack(Track track) => SelectedTrack = track;

        /// <summary>Inject AI orchestrator for script analysis (called from MainWindow).</summary>
        public void SetOrchestrator(IAIAnalysisOrchestrator orchestrator)
        {
            _aiOrchestrator = orchestrator;
        }

        /// <summary>Total width of timeline content (label column + timeline) for alignment. ST-1.</summary>
        public double TimelineContentWidth => TimelineWidth + 56;

        /// <summary>Width for the main audio waveform display (same as timeline width).</summary>
        public double AudioContentWidth => TimelineWidth;

        /// <summary>
        /// Called when PlayheadPosition changes.
        /// CommunityToolkit.Mvvm already fires PropertyChanged automatically.
        /// No debounce re-fire here — Canvas/converters handle their own throttling.
        /// Previously this debounce re-fired PropertyChanged causing double UpdateActivePreview calls.
        /// </summary>
        partial void OnPlayheadPositionChanged(double value)
        {
            // No-op: CommunityToolkit fires PropertyChanged("PlayheadPosition") from the setter.
            // CanvasViewModel throttles its own updates internally.
        }

        public TimelineViewModel(AudioService audioService, ProjectViewModel projectViewModel)
        {
            _audioService = audioService;
            _projectViewModel = projectViewModel;

            // Subscribe to project changes (named method so we can unsubscribe in Dispose)
            _projectViewModel.PropertyChanged += OnProjectViewModelPropertyChanged;

            // Immediately trigger segment/BGM audio when playback starts,
            // so there's no 33ms sync-loop delay on the first frame.
            _audioService.PlaybackStarted += OnPlaybackStarted;

            // Start playhead sync loop
            StartPlayheadSync();
        }

        private void OnProjectViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectViewModel.CurrentProject))
            {
                LoadTracksFromProject();
                ApplyScriptCommand.NotifyCanExecuteChanged();
                AddSegmentWithAssetCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// When main audio playback starts, immediately start any segment/BGM audio
        /// at the current playhead position without waiting for the sync loop.
        /// </summary>
        private void OnPlaybackStarted(object? sender, EventArgs e)
        {
            // Must run synchronously BEFORE _wavePlayer.Play() so mixer inputs
            // (segment + BGM) are added before the first Read() → zero initial offset.
            void DoUpdate()
            {
                var pos = _audioService.GetCurrentPosition();
                UpdateSegmentAudioPlayback(pos);
            }

            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess())
                DoUpdate(); // Already on UI thread — execute immediately
            else
                app.Dispatcher.Invoke(DoUpdate, System.Windows.Threading.DispatcherPriority.Send);
        }

        /// <summary>
        /// Expose the owning ProjectViewModel for panels that need project-level operations (e.g., assets).
        /// </summary>
        public ProjectViewModel ProjectViewModel => _projectViewModel;

        /// <summary>
        /// Load tracks from current project, including all segments for each track.
        /// </summary>
        private void LoadTracksFromProject()
        {
            try
            {
                Tracks.Clear();
                InvalidateActiveSegmentsCache();
                SelectedTrack = null;
                SelectedSegment = null;
                _undoRedo?.Clear(); // New project = clear history

                if (_projectViewModel.CurrentProject?.Tracks == null || _projectViewModel.CurrentProject.Tracks.Count == 0)
                {
                    StatusMessage = "No project loaded or no tracks";
                    return;
                }

                // Get duration from audio service; if no main audio loaded, compute
                // from the latest segment end time so the timeline is still usable.
                var audioDuration = _audioService.GetDuration();
                if (audioDuration > 0)
                    TotalDuration = audioDuration;
                else
                    RecalculateDurationFromSegments();
                RecalculatePixelsPerSecond();

                // Load tracks ordered by Order (display order)
                var sortedTracks = _projectViewModel.CurrentProject.Tracks
                    .OrderBy(t => t.Order)
                    .ToList();

                foreach (var track in sortedTracks)
                {
                    // Use ObservableCollection so UI updates when segments are added/removed (e.g. Add button, drag)
                    if (track.Segments is not ObservableCollection<Segment>)
                        track.Segments = new ObservableCollection<Segment>(track.Segments ?? Array.Empty<Segment>());
                    Tracks.Add(track);
                }

                // Set default selected track (first visual track or first track if none)
                SelectedTrack = sortedTracks.FirstOrDefault(t => t.TrackType == TrackTypes.Visual) ?? sortedTracks.FirstOrDefault();

                StatusMessage = $"Loaded {Tracks.Count} track(s)";
                Log.Information("Tracks loaded: {Count}", Tracks.Count);


                // Enqueue async waveform peak loads for all audio segments
                foreach (var track in Tracks)
                    foreach (var seg in track.Segments)
                        EnqueueSegmentPeakLoad(seg);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading tracks: {ex.Message}";
                Log.Error(ex, "Error loading tracks");
            }
        }



        /// <summary>
        /// Enqueue an async background load of waveform peak data for an audio segment.
        /// Non-blocking: sets Segment.WaveformPeaks on the UI thread when loading completes.
        /// No-op if the segment has no audio asset.
        /// </summary>
        private void EnqueueSegmentPeakLoad(Segment segment)
        {
            if (string.IsNullOrEmpty(segment.BackgroundAssetId))
                return;

            var asset = _projectViewModel?.CurrentProject?.Assets?
                .FirstOrDefault(a => a.Id == segment.BackgroundAssetId);

            if (asset == null || string.IsNullOrEmpty(asset.FilePath)
                || !string.Equals(asset.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                return;

            var path = asset.FilePath;
            var assetDuration = asset.Duration ?? 0;
            _ = Task.Run(() =>
            {
                var peaks = AudioService.GetPeakSamplesFromFile(path, WaveformBinCount);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    segment.WaveformPeaks = peaks;
                    segment.SourceFileDuration = assetDuration;
                });
            });
        }

        /// <summary>
        /// Calculate pixels per second based on timeline width and total duration.
        /// </summary>
        private void RecalculatePixelsPerSecond()
        {
            if (TotalDuration <= 0)
                TotalDuration = 60;

            // Ensure minimum timeline duration so we don't get zero/extreme values
            double displayDuration = Math.Max(TotalDuration + 10, 60); // Add 10s buffer
            PixelsPerSecond = TimelineWidth / displayDuration;

            if (PixelsPerSecond < 1)
                PixelsPerSecond = 1; // Minimum 1px per second
        }

        /// <summary>
        /// Returns the latest EndTime across all segments on all tracks, or 0 if none.
        /// Used to determine timeline duration when no main audio file is loaded.
        /// </summary>
        public double ComputeMaxSegmentEndTime()
        {
            double max = 0;
            foreach (var track in Tracks)
                foreach (var seg in track.Segments)
                    if (seg.EndTime > max)
                        max = seg.EndTime;
            return max;
        }

        /// <summary>
        /// Recalculate <see cref="TotalDuration"/> from segment data when no main audio is loaded.
        /// Falls back to 60s default if there are no segments.
        /// </summary>
        public void RecalculateDurationFromSegments()
        {
            var segEnd = ComputeMaxSegmentEndTime();
            TotalDuration = segEnd > 0 ? segEnd + 5 : 60; // 5s buffer after last segment, or default 60s
            RecalculatePixelsPerSecond();
            TimelineWidth = Math.Max(800, TotalDuration * 10);
        }

        /// <summary>
        /// Convert time (seconds) to pixel position on timeline.
        /// </summary>
        public double TimeToPixels(double timeSeconds)
        {
            return timeSeconds * PixelsPerSecond;
        }

        /// <summary>
        /// Convert pixel position to time (seconds) on timeline.
        /// </summary>
        public double PixelsToTime(double pixelX)
        {
            return pixelX / PixelsPerSecond;
        }

        /// <summary>
        /// Get the source audio/video file duration for a segment's linked asset.
        /// Returns null if no asset linked or asset has no duration info.
        /// </summary>
        public double? GetSourceDurationForSegment(Segment segment)
        {
            if (string.IsNullOrEmpty(segment.BackgroundAssetId))
                return null;
            var asset = _projectViewModel?.CurrentProject?.Assets?
                .FirstOrDefault(a => a.Id == segment.BackgroundAssetId);
            return asset?.Duration;
        }

        /// <summary>
        /// Check if two segments overlap.
        /// </summary>
        private bool HasOverlap(Segment s1, Segment s2)
        {
            // No overlap if s1 ends before s2 starts OR s2 ends before s1 starts
            return !(s1.EndTime <= s2.StartTime || s2.EndTime <= s1.StartTime);
        }

        /// <summary>
        /// Check collision for a segment within a specific track.
        /// </summary>
        private bool CheckCollisionInTrack(Segment segment, string trackId, Segment? excludeSegment = null)
        {
            var track = Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track == null)
                return false;

            foreach (var other in track.Segments)
            {
                if (excludeSegment != null && other.Id == excludeSegment.Id)
                    continue;

                if (HasOverlap(segment, other))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get all segments from all tracks (flattened list).
        /// </summary>
        private List<Segment> GetAllSegmentsFromTracks()
        {
            return Tracks.SelectMany(t => t.Segments).OrderBy(s => s.StartTime).ToList();
        }

        /// <summary>
        /// Get segments for a specific track, ordered by StartTime.
        /// </summary>
        public List<Segment> GetSegmentsForTrack(string trackId)
        {
            return Tracks.FirstOrDefault(t => t.Id == trackId)?.Segments.OrderBy(s => s.StartTime).ToList() ?? new();
        }

        /// <summary>
        /// Snap time value to grid (2 decimal places to match script format).
        /// </summary>
        private double SnapToGrid(double timeSeconds)
        {
            return Math.Round(Math.Round(timeSeconds / GridSize) * GridSize, 2);
        }

        /// <summary>
        /// Add a new visual segment at playhead position. Uses selected track if it is visual,
        /// otherwise uses the first visual track so Add always works (e.g. when user last clicked a text segment).
        /// </summary>
        [RelayCommand]
        public void AddSegmentAtPlayhead()
        {
            try
            {
                if (_projectViewModel.CurrentProject == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                // Use first visual track for adding (Add = visual segment). If user had selected a text segment, still add to Visual 1.
                var targetTrack = SelectedTrack?.TrackType == TrackTypes.Visual
                    ? SelectedTrack
                    : Tracks.FirstOrDefault(t => t.TrackType == TrackTypes.Visual);

                if (targetTrack == null)
                {
                    StatusMessage = "No visual track found";
                    return;
                }

                // Create new segment starting at playhead
                double endTime = PlayheadPosition + DefaultSegmentDurationSeconds;
                if (TotalDuration > 0 && endTime > TotalDuration)
                    endTime = TotalDuration;

                var newSegment = new Segment
                {
                    ProjectId = _projectViewModel.CurrentProject.Id,
                    TrackId = targetTrack.Id,
                    StartTime = SnapToGrid(PlayheadPosition),
                    EndTime = SnapToGrid(endTime),
                    Text = "New Segment",
                    Kind = SegmentKinds.Visual,
                    TransitionType = "fade",
                    TransitionDuration = 0.5,
                    Order = targetTrack.Segments.Count
                };

                if (newSegment.EndTime <= newSegment.StartTime)
                {
                    StatusMessage = "No room at playhead (near end of timeline)";
                    return;
                }

                // Check for collision within the same track
                if (CheckCollisionInTrack(newSegment, targetTrack.Id))
                {
                    StatusMessage = "Segment overlaps with existing segment in this track";
                    return;
                }

                targetTrack.Segments.Add(newSegment);
                InvalidateActiveSegmentsCache();
                SelectSegment(newSegment);
                _undoRedo?.Record(new SegmentAddedAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)targetTrack.Segments,
                    newSegment, InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = targetTrack != SelectedTrack ? "Segment added to Visual 1" : "Segment added";
                Log.Information("Segment added at {StartTime}s in track {TrackId}", newSegment.StartTime, targetTrack.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding segment: {ex.Message}";
                Log.Error(ex, "Error adding segment");
            }
        }

        /// <summary>
        /// Add a new visual segment at playhead with the given asset as background. Used from Elements/Media panel.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanAddSegmentWithAsset))]
        public void AddSegmentWithAsset(Asset? asset)
        {
            if (asset == null) return;
            try
            {
                if (_projectViewModel.CurrentProject == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                // Auto-route to correct track type based on asset type
                string targetTrackType = string.Equals(asset.Type, "Audio", StringComparison.OrdinalIgnoreCase) ? TrackTypes.Audio : TrackTypes.Visual;
                string segmentKind = targetTrackType;

                var targetTrack = (SelectedTrack != null && string.Equals(SelectedTrack.TrackType, targetTrackType, StringComparison.OrdinalIgnoreCase))
                    ? SelectedTrack
                    : Tracks.FirstOrDefault(t => string.Equals(t.TrackType, targetTrackType, StringComparison.OrdinalIgnoreCase));

                // Auto-create track if none exists for the target type
                if (targetTrack == null)
                {
                    AddTrack(targetTrackType);
                    targetTrack = Tracks.LastOrDefault(t => string.Equals(t.TrackType, targetTrackType, StringComparison.OrdinalIgnoreCase));
                    if (targetTrack == null)
                    {
                        StatusMessage = $"Failed to create {targetTrackType} track";
                        return;
                    }
                    _ = _projectViewModel.SaveProjectAsync();
                }

                double segDuration = asset.Duration is > 0 ? asset.Duration.Value : DefaultSegmentDurationSeconds;
                double endTime = PlayheadPosition + segDuration;

                // For audio segments on an empty timeline, extend total duration to fit
                if (TotalDuration <= 0 && segDuration > 0)
                {
                    TotalDuration = endTime;
                    RecalculatePixelsPerSecond();
                }

                if (TotalDuration > 0 && endTime > TotalDuration)
                    endTime = TotalDuration;

                var newSegment = BuildSegment(
                    targetTrack,
                    SnapToGrid(PlayheadPosition),
                    SnapToGrid(endTime),
                    segmentKind,
                    asset.Name ?? "Segment",
                    asset.Id);

                if (newSegment.EndTime <= newSegment.StartTime)
                {
                    StatusMessage = "No room at playhead (near end of timeline)";
                    return;
                }
                if (CheckCollisionInTrack(newSegment, targetTrack.Id))
                {
                    StatusMessage = "Segment overlaps with existing segment in this track";
                    return;
                }

                targetTrack.Segments.Add(newSegment);
                InvalidateActiveSegmentsCache();
                SelectSegment(newSegment);
                EnqueueSegmentPeakLoad(newSegment);
                _undoRedo?.Record(new SegmentAddedAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)targetTrack.Segments,
                    newSegment, InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = "Segment added with media";
                Log.Information("Segment added with asset {AssetId} at {StartTime}s", asset.Id, newSegment.StartTime);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding segment: {ex.Message}";
                Log.Error(ex, "Error adding segment with asset");
            }
        }

        private bool CanAddSegmentWithAsset(Asset? asset) =>
            asset != null && _projectViewModel.CurrentProject != null && Tracks.Count > 0;

        /// <summary>
        /// Create a Segment with standard defaults. Centralises Segment construction to avoid duplication.
        /// </summary>
        private Segment BuildSegment(Track track, double startTime, double endTime, string kind, string text, string? assetId = null)
            => new Segment
            {
                ProjectId = _projectViewModel.CurrentProject!.Id,
                TrackId = track.Id,
                StartTime = startTime,
                EndTime = endTime,
                Text = text,
                BackgroundAssetId = assetId,
                Kind = kind,
                TransitionType = "fade",
                TransitionDuration = 0.5,
                Order = track.Segments.Count
            };

        /// <summary>
        /// Add a segment at a specific time position on a specific track, with an asset.
        /// Used for drag-and-drop from asset panel to timeline.
        /// Returns true if segment was added successfully.
        /// </summary>
        public bool AddSegmentAtPositionOnTrack(Track track, double timeSeconds, Asset asset)
        {
            if (_projectViewModel.CurrentProject == null || track == null || asset == null)
                return false;

            try
            {
                // Determine segment kind based on asset type and track type
                string kind = string.Equals(track.TrackType, "audio", StringComparison.OrdinalIgnoreCase) ? SegmentKinds.Audio : SegmentKinds.Visual;

                // Determine duration: use asset duration if available, else default 5s
                double duration = asset.Duration > 0 ? asset.Duration.Value : 5.0;
                double startTime = SnapToGrid(Math.Max(0, timeSeconds));
                double endTime = SnapToGrid(startTime + duration);

                if (TotalDuration > 0 && endTime > TotalDuration)
                    endTime = TotalDuration;

                var newSegment = BuildSegment(
                    track,
                    startTime,
                    endTime,
                    kind,
                    asset.Name ?? "Segment",
                    asset.Id);

                if (newSegment.EndTime <= newSegment.StartTime)
                {
                    StatusMessage = "No room at drop position";
                    return false;
                }

                if (CheckCollisionInTrack(newSegment, track.Id))
                {
                    StatusMessage = "Segment would overlap with existing segment";
                    return false;
                }

                track.Segments.Add(newSegment);
                InvalidateActiveSegmentsCache();
                SelectSegment(newSegment);
                EnqueueSegmentPeakLoad(newSegment);
                _undoRedo?.Record(new SegmentAddedAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)track.Segments,
                    newSegment, InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = $"Dropped '{asset.Name}' at {startTime:F2}s";
                Log.Information("Segment dropped: asset {AssetId} at {StartTime}s on track {TrackId}", asset.Id, startTime, track.Id);
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error dropping asset: {ex.Message}";
                Log.Error(ex, "Error adding segment from drag-drop");
                return false;
            }
        }

        /// <summary>
        /// Check if an asset type is compatible with a track type.
        /// Audio assets go to audio tracks, image/video to visual tracks.
        /// </summary>
        public static bool IsAssetCompatibleWithTrack(Asset asset, Track track)
        {
            if (asset == null || track == null)
                return false;

            var assetType = asset.Type?.ToLowerInvariant() ?? "";
            var trackType = track.TrackType?.ToLowerInvariant() ?? "";

            return trackType switch
            {
                TrackTypes.Visual => assetType is "image" or "video",
                TrackTypes.Audio => assetType is "audio",
                TrackTypes.Text => false, // Text segments created via script, not drag-drop
                _ => false
            };
        }

        /// <summary>
        /// Create a segment on the appropriate track for a canvas element.
        /// Returns the new Segment, or null if creation failed.
        /// Used by CanvasViewModel to link elements to timeline segments.
        /// </summary>
        public Segment? CreateSegmentForElement(string trackType, string text, double duration = 5.0)
        {
            if (_projectViewModel?.CurrentProject == null)
                return null;

            var targetTrack = Tracks.FirstOrDefault(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase));
            if (targetTrack == null)
            {
                AddTrack(trackType);
                targetTrack = Tracks.LastOrDefault(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase));
                if (targetTrack == null)
                    return null;
            }

            double startTime = SnapToGrid(PlayheadPosition);
            double endTime = SnapToGrid(startTime + duration);
            if (TotalDuration > 0 && endTime > TotalDuration)
                endTime = TotalDuration;

            var newSegment = BuildSegment(targetTrack, startTime, endTime, trackType, text);

            if (newSegment.EndTime <= newSegment.StartTime)
                return null;

            if (CheckCollisionInTrack(newSegment, targetTrack.Id))
            {
                StatusMessage = $"Element segment overlaps existing segment";
                return null;
            }

            targetTrack.Segments.Add(newSegment);
            InvalidateActiveSegmentsCache();
            Log.Information("Created element segment '{Text}' on {TrackType} track at {Start}s-{End}s", text, trackType, startTime, endTime);
            return newSegment;
        }

        /// <summary>
        /// Clear all segments in the selected track.
        /// </summary>
        [RelayCommand]
        public void ClearAllSegments()
        {
            try
            {
                if (SelectedTrack == null)
                {
                    StatusMessage = "No track selected";
                    return;
                }

                if (SelectedTrack.Segments.Count == 0)
                {
                    StatusMessage = "Track already empty";
                    return;
                }

                int count = SelectedTrack.Segments.Count;
                SelectedTrack.Segments.Clear();
                SelectedSegment = null;
                StatusMessage = $"Cleared {count} segment(s) from {SelectedTrack.Name}";
                Log.Information("Cleared all segments from track {TrackId}: {Count}", SelectedTrack.Id, count);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error clearing segments: {ex.Message}";
                Log.Error(ex, "Error clearing segments");
            }
        }

        /// <summary>
        /// Set selection sync service for bidirectional canvas↔timeline selection.
        /// </summary>
        public void SetSelectionSyncService(SelectionSyncService selectionSyncService)
        {
            _selectionSyncService = selectionSyncService;

            // When canvas element is selected, highlight corresponding segment on timeline
            _selectionSyncService.CanvasSelectionChanged += OnCanvasElementSelected;
        }

        private void OnCanvasElementSelected(string? segmentId)
        {
            if (string.IsNullOrEmpty(segmentId))
                return;

            // Find segment across all tracks
            foreach (var track in Tracks)
            {
                foreach (var seg in track.Segments)
                {
                    if (string.Equals(seg.Id, segmentId, StringComparison.Ordinal))
                    {
                        SelectedSegment = seg;
                        SelectedTrack = track;
                        StatusMessage = $"Selected: {seg.Text}";
                        return;
                    }
                }
            }
        }

        // NOTE: Play/Pause/Stop commands have been moved to CanvasViewModel and AudioPlayerViewModel
        // Use TogglePlayPauseCommand instead via the CanvasView playback control

        /// <summary>
        /// Delete selected segment (or all multi-selected segments if any are selected).
        /// </summary>
        [RelayCommand]
        public void DeleteSelectedSegment()
        {
            try
            {
                // Multi-select delete takes priority
                if (_multiSelectedSegments.Count > 0)
                {
                    int totalCount = _multiSelectedSegments.Count;
                    // Skip segments whose track is locked
                    var toDelete = _multiSelectedSegments
                        .Where(seg => Tracks.FirstOrDefault(t => t.Id == seg.TrackId)?.IsLocked != true)
                        .ToList();
                    int skipped = totalCount - toDelete.Count;
                    ClearMultiSelection();

                    var actions = new System.Collections.Generic.List<IUndoableAction>();
                    foreach (var seg in toDelete)
                    {
                        var track = Tracks.FirstOrDefault(t => t.Id == seg.TrackId);
                        if (track?.Segments is System.Collections.ObjectModel.ObservableCollection<Segment> segs)
                        {
                            int idx = segs.IndexOf(seg);
                            segs.Remove(seg);
                            if (SelectedSegment == seg) SelectedSegment = null;
                            actions.Add(new SegmentDeletedAction(
                                segs, seg, idx,
                                InvalidateActiveSegmentsCache,
                                s => { if (s != null) SelectSegment(s); else SelectedSegment = null; }));
                        }
                    }
                    if (actions.Count > 0)
                        _undoRedo?.Record(new CompoundAction($"Delete {actions.Count} segment(s)", actions));
                    InvalidateActiveSegmentsCache();
                    StatusMessage = skipped > 0
                        ? $"{toDelete.Count} segment(s) deleted ({skipped} skipped — locked)"
                        : $"{toDelete.Count} segment(s) deleted";
                    return;
                }

                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                if (SelectedTrack == null)
                {
                    StatusMessage = "No track context";
                    return;
                }

                var deletedSeg = SelectedSegment;
                var deletedTrackSegs = (System.Collections.ObjectModel.ObservableCollection<Segment>)SelectedTrack.Segments;
                var deletedIndex = deletedTrackSegs.IndexOf(deletedSeg);
                SelectedTrack.Segments.Remove(deletedSeg);
                InvalidateActiveSegmentsCache();
                SelectedSegment = null;
                _undoRedo?.Record(new SegmentDeletedAction(
                    deletedTrackSegs, deletedSeg, deletedIndex,
                    InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = "Segment deleted";
                Log.Information("Segment deleted from track {TrackId}", SelectedTrack.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting segment: {ex.Message}";
                Log.Error(ex, "Error deleting segment");
            }
        }

        /// <summary>
        /// Select all segments across all tracks (Ctrl+A).
        /// </summary>
        [RelayCommand]
        public void SelectAllSegments()
        {
            ClearMultiSelection();
            foreach (var track in Tracks)
            {
                foreach (var seg in track.Segments)
                {
                    seg.IsMultiSelected = true;
                    _multiSelectedSegments.Add(seg);
                }
            }
            int total = _multiSelectedSegments.Count;
            StatusMessage = total > 0 ? $"All {total} segment(s) selected" : "No segments to select";
        }

        /// <summary>
        /// Duplicate selected segment with offset, in the same track.
        /// </summary>
        [RelayCommand]
        public void DuplicateSelectedSegment()
        {
            try
            {
                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                if (SelectedTrack == null)
                {
                    StatusMessage = "No track context";
                    return;
                }

                var original = SelectedSegment;
                double duration = original.EndTime - original.StartTime;

                var duplicate = new Segment
                {
                    ProjectId = original.ProjectId,
                    TrackId = original.TrackId,
                    StartTime = SnapToGrid(original.EndTime + 0.5),
                    EndTime = SnapToGrid(original.EndTime + 0.5 + duration),
                    Text = original.Text + " (Copy)",
                    Kind = original.Kind,
                    BackgroundAssetId = original.BackgroundAssetId,
                    TransitionType = original.TransitionType,
                    TransitionDuration = original.TransitionDuration,
                    Order = SelectedTrack.Segments.Count
                };

                if (CheckCollisionInTrack(duplicate, SelectedTrack.Id))
                {
                    StatusMessage = "Duplicate would overlap with existing segment in this track";
                    return;
                }

                SelectedTrack.Segments.Add(duplicate);
                InvalidateActiveSegmentsCache();
                SelectSegment(duplicate);
                _undoRedo?.Record(new SegmentAddedAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)SelectedTrack.Segments,
                    duplicate, InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = "Segment duplicated";
                Log.Information("Segment duplicated in track {TrackId}", SelectedTrack.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error duplicating segment: {ex.Message}";
                Log.Error(ex, "Error duplicating segment");
            }
        }

        /// <summary>
        /// Split the selected segment at the current playhead position (Ctrl+B).
        /// Creates two segments: [original.Start → playhead] and [playhead → original.End].
        /// </summary>
        [RelayCommand]
        public void SplitSelectedSegmentAtPlayhead()
        {
            try
            {
                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                if (SelectedTrack == null)
                {
                    StatusMessage = "No track context";
                    return;
                }

                var segment = SelectedSegment;
                var splitTime = SnapToGrid(PlayheadPosition);

                // Validate: playhead must be inside segment (with margin)
                const double margin = 0.05; // 50ms minimum segment duration
                if (splitTime <= segment.StartTime + margin || splitTime >= segment.EndTime - margin)
                {
                    StatusMessage = "Playhead must be inside the segment to split";
                    return;
                }

                // Create the right half (new segment)
                var rightHalf = new Segment
                {
                    ProjectId = segment.ProjectId,
                    TrackId = segment.TrackId,
                    StartTime = splitTime,
                    EndTime = segment.EndTime,
                    Text = segment.Text,
                    Kind = segment.Kind,
                    BackgroundAssetId = segment.BackgroundAssetId,
                    TransitionType = segment.TransitionType,
                    TransitionDuration = segment.TransitionDuration,
                    Volume = segment.Volume,
                    FadeInDuration = 0, // Right half starts clean
                    FadeOutDuration = segment.FadeOutDuration,
                    Order = SelectedTrack.Segments.Count
                };

                // Trim the left half (modify existing segment)
                segment.EndTime = splitTime;
                segment.FadeOutDuration = 0; // Left half ends clean

                // Add right half to the track
                double originalEndBeforeSplit = rightHalf.EndTime; // same as segment's original EndTime
                SelectedTrack.Segments.Add(rightHalf);
                InvalidateActiveSegmentsCache();
                SelectSegment(rightHalf);
                _undoRedo?.Record(new SegmentSplitAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)SelectedTrack.Segments,
                    segment, originalEndBeforeSplit, rightHalf,
                    InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
                StatusMessage = $"Segment split at {splitTime:F2}s";
                Log.Information("Segment split at {SplitTime}s in track {TrackId}", splitTime, SelectedTrack.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error splitting segment: {ex.Message}";
                Log.Error(ex, "Error splitting segment");
            }
        }

        /// <summary>
        /// Add a new track of the specified type to the project.
        /// </summary>
        public void AddTrack(string trackType, string? name = null)
        {
            if (_projectViewModel?.CurrentProject == null)
                return;

            var maxOrder = Tracks.Count > 0 ? Tracks.Max(t => t.Order) + 1 : 0;
            var typeName = trackType switch
            {
                TrackTypes.Text => "Text",
                TrackTypes.Visual => "Visual",
                TrackTypes.Audio => "Audio",
                _ => trackType
            };

            var track = new Track
            {
                ProjectId = _projectViewModel.CurrentProject.Id,
                Order = maxOrder,
                TrackType = trackType,
                Name = name ?? $"{typeName} {Tracks.Count(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase)) + 1}",
                IsVisible = true,
                IsLocked = false,
                Segments = new ObservableCollection<Segment>()
            };

            Tracks.Add(track);

            // Also add to the project model so it persists on next save
            _projectViewModel.CurrentProject.Tracks ??= new List<Track>();
            _projectViewModel.CurrentProject.Tracks.Add(track);

            StatusMessage = $"Added track: {track.Name}";
            Log.Information("Track added: {Name} ({Type}) at Order {Order}", track.Name, trackType, maxOrder);
        }

        /// <summary>Toggle IsLocked on a track. Called from track header lock button.</summary>
        public void ToggleTrackLock(Track? track)
        {
            if (track == null) return;
            track.IsLocked = !track.IsLocked;
            StatusMessage = track.IsLocked ? $"Track '{track.Name}' locked" : $"Track '{track.Name}' unlocked";
            _ = _projectViewModel.SaveProjectAsync();
        }

        /// <summary>Toggle IsVisible on a track. Called from track header eye button.</summary>
        public void ToggleTrackVisibility(Track? track)
        {
            if (track == null) return;
            track.IsVisible = !track.IsVisible;
            StatusMessage = track.IsVisible ? $"Track '{track.Name}' visible" : $"Track '{track.Name}' hidden";
            _ = _projectViewModel.SaveProjectAsync();
        }

        /// <summary>Persist the current project. Called by views after making in-place edits (e.g. transition picker).</summary>
        public void RequestProjectSave() => _ = _projectViewModel.SaveProjectAsync();

        // ── Multi-select state ──────────────────────────────────────────────────

        private readonly List<Segment> _multiSelectedSegments = new();

        /// <summary>
        /// Return true when one or more segments are in the multi-selection.
        /// </summary>
        public bool HasMultiSelection => _multiSelectedSegments.Count > 0;

        /// <summary>
        /// Toggle <paramref name="segment"/> in/out of the multi-selection without
        /// changing <see cref="SelectedSegment"/> (single-select is kept independent).
        /// </summary>
        public void ToggleMultiSelect(Segment segment)
        {
            if (_multiSelectedSegments.Contains(segment))
            {
                _multiSelectedSegments.Remove(segment);
                segment.IsMultiSelected = false;
            }
            else
            {
                _multiSelectedSegments.Add(segment);
                segment.IsMultiSelected = true;
            }
            StatusMessage = _multiSelectedSegments.Count > 0
                ? $"{_multiSelectedSegments.Count} segment(s) selected"
                : "Multi-selection cleared";
        }

        /// <summary>Clear all multi-selected segments without deleting them.</summary>
        public void ClearMultiSelection()
        {
            foreach (var seg in _multiSelectedSegments)
                seg.IsMultiSelected = false;
            _multiSelectedSegments.Clear();
        }

        /// <summary>
        /// Magnetic snap: if <paramref name="proposedTime"/> is within <paramref name="thresholdSeconds"/>
        /// of any segment edge in the same track (excluding <paramref name="excludeSegmentId"/>),
        /// return the snapped edge time; otherwise return <paramref name="proposedTime"/>.
        /// </summary>
        public double SnapToSegmentEdge(double proposedTime, string? trackId, string? excludeSegmentId, double thresholdSeconds)
        {
            if (trackId == null) return proposedTime;
            var track = Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track == null) return proposedTime;
            double best = proposedTime;
            double bestDist = thresholdSeconds;
            foreach (var seg in track.Segments)
            {
                if (seg.Id == excludeSegmentId) continue;
                double dStart = Math.Abs(proposedTime - seg.StartTime);
                double dEnd   = Math.Abs(proposedTime - seg.EndTime);
                if (dStart < bestDist) { bestDist = dStart; best = seg.StartTime; }
                if (dEnd   < bestDist) { bestDist = dEnd;   best = seg.EndTime; }
            }
            return best;
        }

        /// <summary>
        /// Remove a track (must be empty — no segments). Audio tracks with waveform-only are removable.
        /// </summary>
        public bool RemoveTrack(Track track)
        {
            if (track == null)
                return false;

            if (track.Segments != null && track.Segments.Count > 0)
            {
                StatusMessage = "Cannot remove track with segments. Delete segments first.";
                return false;
            }

            Tracks.Remove(track);

            // Re-index remaining track orders
            int order = 0;
            foreach (var t in Tracks.OrderBy(t => t.Order))
                t.Order = order++;

            StatusMessage = $"Removed track: {track.Name}";
            Log.Information("Track removed: {Name} ({Type})", track.Name, track.TrackType);
            return true;
        }

        /// <summary>
        /// Update segment timing with collision check within the same track.
        /// When overlap detected, snaps to nearest valid boundary (edge of blocking segment) to prevent jitter.
        /// </summary>
        public bool UpdateSegmentTiming(Segment segment, double newStartTime, double newEndTime)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(segment.TrackId))
                {
                    StatusMessage = "Segment has no track";
                    return false;
                }

                var track = Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                if (track == null)
                {
                    StatusMessage = "Track not found";
                    return false;
                }

                double duration = newEndTime - newStartTime;
                if (duration <= 0)
                {
                    StatusMessage = "Invalid segment timing";
                    return false;
                }

                const double timeTolerance = 0.001;
                bool resizeRightOnly = Math.Abs(newStartTime - segment.StartTime) < timeTolerance && newEndTime > segment.EndTime;
                bool resizeLeftOnly = Math.Abs(newEndTime - segment.EndTime) < timeTolerance;

                if (resizeRightOnly)
                {
                    newStartTime = segment.StartTime;
                    if (newEndTime > TotalDuration)
                        newEndTime = TotalDuration;
                    newEndTime = SnapToGrid(newEndTime);
                }
                else if (resizeLeftOnly)
                {
                    newEndTime = segment.EndTime;
                    if (newStartTime < 0)
                        newStartTime = 0;
                    newStartTime = SnapToGrid(newStartTime);
                }
                else
                {
                    if (newEndTime > TotalDuration)
                    {
                        newEndTime = TotalDuration;
                        newStartTime = newEndTime - duration;
                    }
                    if (newStartTime < 0)
                    {
                        newStartTime = 0;
                        newEndTime = duration;
                    }
                    newStartTime = SnapToGrid(newStartTime);
                    newEndTime = SnapToGrid(newEndTime);
                }

                var testSegment = new Segment
                {
                    Id = segment.Id,
                    StartTime = newStartTime,
                    EndTime = newEndTime
                };

                if (CheckCollisionInTrack(testSegment, track.Id, segment))
                {
                    if (resizeRightOnly)
                    {
                        double capAt = newEndTime;
                        foreach (var other in track.Segments)
                        {
                            if (other.Id == segment.Id) continue;
                            if (other.StartTime > segment.StartTime)
                                capAt = Math.Min(capAt, other.StartTime);
                        }
                        newStartTime = segment.StartTime;
                        newEndTime = Math.Min(SnapToGrid(newEndTime), capAt);
                        if (newEndTime <= newStartTime)
                        {
                            StatusMessage = "Cannot extend: blocked by next segment";
                            return false;
                        }
                    }
                    else if (resizeLeftOnly)
                    {
                        double floorAt = newStartTime;
                        foreach (var other in track.Segments)
                        {
                            if (other.Id == segment.Id) continue;
                            if (other.EndTime < segment.EndTime)
                                floorAt = Math.Max(floorAt, other.EndTime);
                        }
                        newEndTime = segment.EndTime;
                        newStartTime = Math.Max(SnapToGrid(newStartTime), floorAt);
                        if (newEndTime <= newStartTime)
                        {
                            StatusMessage = "Cannot extend: blocked by previous segment";
                            return false;
                        }
                    }
                    else
                    {
                        var snapped = TrySnapToBoundary(segment, newStartTime, newEndTime, track);
                        if (snapped.HasValue)
                        {
                            newStartTime = snapped.Value.start;
                            newEndTime = snapped.Value.end;
                        }
                        else
                        {
                            StatusMessage = "Cannot move: overlaps with other segment in track";
                            return false;
                        }
                    }
                }

                // Apply changes
                segment.StartTime = newStartTime;
                segment.EndTime = newEndTime;

                StatusMessage = "Segment updated";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error updating segment: {ex.Message}";
                Log.Error(ex, "Error updating segment");
                return false;
            }
        }

        /// <summary>
        /// Snap segment to valid boundary within the same track when overlap detected.
        /// </summary>
        private (double start, double end)? TrySnapToBoundary(Segment segment, double requestedStart, double requestedEnd, Track track)
        {
            double duration = requestedEnd - requestedStart;
            double currentStart = segment.StartTime;
            (double start, double end)? best = null;
            double bestDistance = double.MaxValue;

            foreach (var other in track.Segments)
            {
                if (other.Id == segment.Id) continue;

                // Option A: Place after other segment (allow clamp to end of timeline)
                double startA = SnapToGrid(other.EndTime);
                double endA = startA + duration;
                if (endA > TotalDuration && TotalDuration > 0)
                {
                    endA = TotalDuration;
                    startA = SnapToGrid(endA - duration);
                }
                if (startA >= 0 && endA <= TotalDuration + 0.001)
                {
                    var testA = new Segment { Id = segment.Id, StartTime = startA, EndTime = endA };
                    if (!CheckCollisionInTrack(testA, track.Id, segment))
                    {
                        double dist = Math.Abs(startA - currentStart);
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            best = (startA, endA);
                        }
                    }
                }

                // Option B: Place before other segment (allow clamp to start of timeline)
                double endB = SnapToGrid(other.StartTime);
                double startB = endB - duration;
                if (startB < 0)
                {
                    startB = 0;
                    endB = SnapToGrid(duration);
                }
                if (startB >= 0 && endB <= TotalDuration + 0.001)
                {
                    var testB = new Segment { Id = segment.Id, StartTime = startB, EndTime = endB };
                    if (!CheckCollisionInTrack(testB, track.Id, segment))
                    {
                        double dist = Math.Abs(startB - currentStart);
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            best = (startB, endB);
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Select a segment for editing and set its track as selected.
        /// Notifies SelectionSyncService for canvas↔timeline sync.
        /// </summary>
        public void SelectSegment(Segment segment)
        {
            SelectedSegment = segment;
            
            // Also select the track containing this segment
            if (!string.IsNullOrWhiteSpace(segment.TrackId))
            {
                SelectedTrack = Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
            }
            
            // Notify selection sync: check if playhead is within segment bounds
            bool playheadInRange = PlayheadPosition >= segment.StartTime && PlayheadPosition < segment.EndTime;
            _selectionSyncService?.NotifyTimelineSegmentSelected(segment.Id, playheadInRange);
            
            StatusMessage = $"Selected: {segment.Text}";
        }

        /// <summary>
        /// Get active segments at the given time across all tracks, ordered by track.Order (top to bottom).
        /// Results are cached per exact playhead time: safe within a single UI dispatch frame.
        /// Callers must not mutate the returned list.
        /// </summary>
        public List<(Track track, Segment segment)> GetActiveSegmentsAtTime(double timeSeconds)
        {
            // Return cached result when called multiple times for the same position (e.g. canvas + audio per frame)
            if (_cachedActiveSegments != null && timeSeconds == _cachedActiveSegmentsTime)
                return _cachedActiveSegments;

            var result = new List<(Track track, Segment segment)>(4);

            foreach (var track in Tracks)
            {
                if (track.Segments == null || !track.IsVisible)
                    continue;

                foreach (var s in track.Segments)
                {
                    if (s.StartTime <= timeSeconds && timeSeconds < s.EndTime)
                    {
                        result.Add((track, s));
                        break; // Only one active segment per track
                    }
                }
            }

            // Sort only if more than 1 result (common case: 1-2 tracks)
            if (result.Count > 1)
                result.Sort((a, b) => a.track.Order != b.track.Order
                    ? a.track.Order.CompareTo(b.track.Order)
                    : a.segment.StartTime.CompareTo(b.segment.StartTime));

            _cachedActiveSegmentsTime = timeSeconds;
            _cachedActiveSegments = result;
            return result;
        }

        /// <summary>
        /// Invalidate the active segments cache. Call when tracks or segments change.
        /// </summary>
        private void InvalidateActiveSegmentsCache() => _cachedActiveSegments = null;

        /// <summary>Public wrapper used by TimelineView drag-undo callbacks.</summary>
        public void InvalidateActiveSegmentsCachePublic() => InvalidateActiveSegmentsCache();

        /// <summary>
        /// Seek playhead (and audio) to the specified position in seconds.
        /// Must be called when user clicks on timeline - otherwise sync loop overwrites PlayheadPosition.
        /// </summary>
        public void SeekTo(double positionSeconds)
        {
            try
            {
                positionSeconds = Math.Clamp(positionSeconds, 0, TotalDuration);
                _audioService.Seek(positionSeconds);
                PlayheadPosition = positionSeconds;
                _lastSyncedPlayhead = positionSeconds;
                // Immediately resync segment + BGM to the new position (forceResync=true)
                UpdateSegmentAudioPlayback(positionSeconds, forceResync: true);
                StatusMessage = $"Playhead: {positionSeconds:F1}s";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Seek failed: {ex.Message}";
                Log.Warning(ex, "Seek failed");
            }
        }

        /// <summary>
        /// Update playhead immediately for scrub preview without forcing costly audio seek.
        /// </summary>
        public void PreviewPlayhead(double positionSeconds)
        {
            _isScrubbing = true;
            positionSeconds = Math.Clamp(positionSeconds, 0, TotalDuration);
            PlayheadPosition = positionSeconds;
            StatusMessage = $"Preview: {positionSeconds:F1}s";
        }

        /// <summary>
        /// Commit a seek after scrub gesture ends.
        /// </summary>
        public void CommitScrubSeek(double positionSeconds)
        {
            SeekTo(positionSeconds);
            _isScrubbing = false;
        }

        /// <summary>
        /// Start playhead position sync with audio playback.
        /// </summary>
        private void StartPlayheadSync()
        {
            _isPlayheadSyncing = true;
            _playheadSyncCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                while (_isPlayheadSyncing && !_playheadSyncCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_audioService.IsPlaying || _isScrubbing)
                        {
                            await Task.Delay(IdleSyncIntervalMs, _playheadSyncCts.Token);
                            continue;
                        }

                        var currentPosition = _audioService.GetCurrentPosition();
                        
                        // Clamp to total duration to prevent playhead overshoot
                        if (TotalDuration > 0 && currentPosition > TotalDuration)
                            currentPosition = TotalDuration;

                        if (Math.Abs(currentPosition - _lastSyncedPlayhead) < PositionDeltaThreshold)
                        {
                            await Task.Delay(ActiveSyncIntervalMs, _playheadSyncCts.Token);
                            continue;
                        }
                        _lastSyncedPlayhead = currentPosition;

                        var app = Application.Current;
                        if (app == null) break;
                        await app.Dispatcher.InvokeAsync(() =>
                        {
                            PlayheadPosition = currentPosition;
                            UpdateSegmentAudioPlayback(currentPosition);
                        }, System.Windows.Threading.DispatcherPriority.Background);
                        
                        await Task.Delay(ActiveSyncIntervalMs, _playheadSyncCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error in playhead sync loop");
                    }
                }
            }, _playheadSyncCts.Token);

            Log.Information("Playhead sync started");
        }

        /// <summary>
        /// Stop playhead position sync.
        /// </summary>
        private void StopPlayheadSync()
        {
            _isPlayheadSyncing = false;
            _playheadSyncCts?.Cancel();
            _playheadSyncCts?.Dispose();
            _audioService.StopSegmentAudio();
            Log.Information("Playhead sync stopped");
        }

        /// <summary>
        /// Check active audio segments at current playhead and play/stop segment audio accordingly.
        /// Also handles BGM preview playback and preloading upcoming audio segments.
        /// Called from playhead sync loop during playback.
        /// Set forceResync=true after an explicit seek to resync reader positions.
        /// </summary>
        private void UpdateSegmentAudioPlayback(double playheadSeconds, bool forceResync = false)
        {
            try
            {
                var activeSegments = GetActiveSegmentsAtTime(playheadSeconds);

                // Find first active audio segment
                Segment? audioSegment = null;
                Track? audioTrack = null;
                foreach (var (track, segment) in activeSegments)
                {
                    if (string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(segment.BackgroundAssetId))
                    {
                        audioSegment = segment;
                        audioTrack = track;
                        break;
                    }
                }

                if (audioSegment == null)
                {
                    // No active audio segment — stop any playing segment audio
                    if (_audioService.CurrentSegmentId != null)
                    {
                        Log.Debug("No active audio segment at {Time}s, stopping segment audio", playheadSeconds);
                        _audioService.StopSegmentAudio();
                    }

                    // Pre-load the next upcoming audio segment for near-zero latency start
                    PreloadNextAudioSegment(playheadSeconds);
                }
                else
                {
                    // Find asset file path
                    var asset = _projectViewModel?.CurrentProject?.Assets?
                        .FirstOrDefault(a => a.Id == audioSegment.BackgroundAssetId);
                    if (asset == null || string.IsNullOrEmpty(asset.FilePath))
                    {
                        Log.Debug("Audio segment {SegId} has no asset (AssetId={AssetId}, AssetsCount={Count})",
                            audioSegment.Id, audioSegment.BackgroundAssetId,
                            _projectViewModel?.CurrentProject?.Assets?.Count ?? -1);
                        _audioService.StopSegmentAudio();
                    }
                    else
                    {
                        // Calculate volume with fade in/out
                        float volume = CalculateSegmentVolume(audioSegment, playheadSeconds);

                        // Play or update segment audio
                        _audioService.PlaySegmentAudio(
                            audioSegment.Id,
                            asset.FilePath,
                            audioSegment.StartTime,
                            playheadSeconds,
                            volume,
                            audioSegment.SourceStartOffset,
                            forceResync);

                        // Also preload next segment while current one is playing
                        // so transitions are seamless (no file I/O at transition point)
                        PreloadNextAudioSegment(playheadSeconds);
                    }
                }

            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error in segment audio playback update");
            }
        }

        /// <summary>
        /// Pre-load the next upcoming audio segment so playback starts with minimal latency.
        /// Scans audio tracks for the earliest segment that starts after the current playhead.
        /// </summary>
        private void PreloadNextAudioSegment(double playheadSeconds)
        {
            try
            {
                Segment? nextSeg = null;
                foreach (var track in Tracks)
                {
                    if (!track.IsVisible || !string.Equals(track.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (track.Segments == null) continue;
                    foreach (var s in track.Segments)
                    {
                        if (s.StartTime > playheadSeconds && !string.IsNullOrEmpty(s.BackgroundAssetId))
                        {
                            if (nextSeg == null || s.StartTime < nextSeg.StartTime)
                                nextSeg = s;
                            // Don't break — ObservableCollection is not necessarily sorted by StartTime
                        }
                    }
                }

                if (nextSeg != null)
                {
                    var asset = _projectViewModel?.CurrentProject?.Assets?
                        .FirstOrDefault(a => a.Id == nextSeg.BackgroundAssetId);
                    if (asset != null && !string.IsNullOrEmpty(asset.FilePath))
                        _audioService.PreloadSegmentAudio(nextSeg.Id, asset.FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error preloading next audio segment");
            }
        }

        /// <summary>
        /// Calculate effective volume for a segment at a given time, applying fade in/out curves.
        /// </summary>
        private static float CalculateSegmentVolume(Segment segment, double playheadSeconds)
        {
            float baseVolume = (float)segment.Volume;
            double elapsed = playheadSeconds - segment.StartTime;
            double remaining = segment.EndTime - playheadSeconds;

            // Fade in
            if (segment.FadeInDuration > 0 && elapsed < segment.FadeInDuration)
            {
                float fadeRatio = (float)(elapsed / segment.FadeInDuration);
                baseVolume *= fadeRatio;
            }

            // Fade out
            if (segment.FadeOutDuration > 0 && remaining < segment.FadeOutDuration)
            {
                float fadeRatio = (float)(remaining / segment.FadeOutDuration);
                baseVolume *= fadeRatio;
            }

            return Math.Clamp(baseVolume, 0f, 1f);
        }

        /// <summary>
        /// Min/max timeline width for zoom (Ctrl+wheel).
        /// </summary>
        public const double MinTimelineWidth = 400;
        public const double MaxTimelineWidth = 20000;

        /// <summary>
        /// Zoom timeline by factor (e.g. 1.15 = zoom in, 1/1.15 = zoom out). Call from Ctrl+MouseWheel.
        /// </summary>
        public void ZoomBy(double factor)
        {
            double newWidth = TimelineWidth * factor;
            newWidth = Math.Clamp(newWidth, MinTimelineWidth, MaxTimelineWidth);
            if (Math.Abs(newWidth - TimelineWidth) < 1)
                return;
            TimelineWidth = newWidth;
        }

        /// <summary>
        /// Handle timeline width change (from UI resize or zoom).
        /// </summary>
        partial void OnTimelineWidthChanged(double value)
        {
            RecalculatePixelsPerSecond();
            OnPropertyChanged(nameof(TimelineContentWidth));
        }

        partial void OnPixelsPerSecondChanged(double value)
        {
            OnPropertyChanged(nameof(AudioContentWidth));
        }

        /// <summary>
        /// Handle total duration change.
        /// </summary>
        partial void OnTotalDurationChanged(double value)
        {
            RecalculatePixelsPerSecond();
            OnPropertyChanged(nameof(AudioContentWidth));
        }

        /// <summary>
        /// Cleanup on dispose.
        /// </summary>
        /// <summary>
        /// Refresh audio peak data. Currently a no-op as per-segment waveforms are used.
        /// Called after an audio file is loaded to trigger waveform display update.
        /// </summary>
        public Task RefreshAudioPeaksAsync() => Task.CompletedTask;

        public void Dispose()
        {
            _audioService.PlaybackStarted -= OnPlaybackStarted;
            _projectViewModel.PropertyChanged -= OnProjectViewModelPropertyChanged;
            StopPlayheadSync();
        }
    }

}
