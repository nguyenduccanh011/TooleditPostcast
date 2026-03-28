#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Helpers;
using PodcastVideoEditor.Ui.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
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
    public partial class TimelineViewModel : ObservableObject, IDisposable
    {
        private readonly IAudioTimelinePreviewService _audioService;
        private readonly ProjectViewModel _projectViewModel;
        private SelectionSyncService? _selectionSyncService;
        private volatile bool _isScrubbing;
        private long _lastSyncedPlayheadBits = BitConverter.DoubleToInt64Bits(-1);

        // Dedicated services for multi-track segment audio and playback coordination.
        // These replace the previous inline sync loop and single-segment UpdateSegmentAudioPlayback.
        private readonly TimelineAudioPreviewService _audioPreviewService;
        private readonly TimelinePlaybackCoordinator _playbackCoordinator;

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
        private double totalDuration = TimelineConstants.DefaultEmptyDuration; // seconds (from audio)

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

        private const double DefaultSegmentDurationSeconds = TimelineConstants.DefaultSegmentDuration;
        // Cache for GetActiveSegmentsAtTime: avoid duplicate allocations within the same dispatch frame.
        // CanvasViewModel and the audio playback loop both call this per frame.
        private double _cachedActiveSegmentsTime = double.MinValue;
        private List<(Track track, Segment segment)>? _cachedActiveSegments;

        // Per-track binary-search index for O(log n) segment lookup.
        // Rebuilt lazily when _segmentIndexDirty is true.
        private Dictionary<string, SegmentIntervalIndex>? _segmentIndexMap;
        private bool _segmentIndexDirty = true;

        // Extracted services — pure-logic, no UI dependencies.
        private readonly SegmentSnapService _snapService = new();

        /// <summary>Limits concurrent waveform peak-loading tasks to avoid thread pool starvation.</summary>
        private static readonly SemaphoreSlim _peakLoadThrottle = new(3, 3);
        private readonly TimelineLayoutService _layoutService = new();

        // Undo/redo (optional — set via SetUndoRedoService from MainViewModel).
        private UndoRedoService? _undoRedo;

        /// <summary>Expose so TimelineView can record drag-complete timing changes.</summary>
        public UndoRedoService? UndoRedoService => _undoRedo;

        /// <summary>
        /// Wire up undo/redo. Called from MainViewModel after construction.
        /// </summary>
        public void SetUndoRedoService(UndoRedoService service) => _undoRedo = service;

        /// <summary>Select the given track (called from TimelineView code-behind). Clears SelectedSegment and SelectedElement so TrackPanel takes priority.</summary>
        public void SelectTrack(Track track)
        {
            SelectedSegment = null;
            SelectedTrack = track;
            // Clear canvas element selection so UpdateVisibility() shows TrackPanel
            _selectionSyncService?.NotifyTimelineSegmentSelected(null, false);
        }

        /// <summary>Inject AI orchestrator for script analysis (called from MainWindow).</summary>
        public void SetOrchestrator(IAIAnalysisOrchestrator orchestrator)
        {
            _aiOrchestrator = orchestrator;
        }

        /// <summary>Total width of timeline content (label column + timeline) for alignment. ST-1.</summary>
        public double TimelineContentWidth => TimelineWidth + 56;

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

        public TimelineViewModel(IAudioTimelinePreviewService audioService, ProjectViewModel projectViewModel)
        {
            _audioService = audioService;
            _projectViewModel = projectViewModel;

            // Subscribe to project changes (named method so we can unsubscribe in Dispose)
            _projectViewModel.PropertyChanged += OnProjectViewModelPropertyChanged;

            // Multi-track audio preview: properly tracks active segment IDs so leaving
            // segments are stopped individually (fixes segment-overlap bleed bug).
            _audioPreviewService = new TimelineAudioPreviewService(
                audioService,
                GetActiveSegmentsAtTime,
                () => Tracks,
                () => _projectViewModel.CurrentProject,
                () => TotalDuration);

            // Playback coordinator: drives the sync loop, wall-clock fallback, loop/EOF,
            // and dispatches to UI thread — replacing the old inline Task.Run loop.
            _playbackCoordinator = new TimelinePlaybackCoordinator(
                audioService,
                new WpfPlaybackDispatcher(),
                () => _isScrubbing,
                () => IsLoopPlayback,
                () => TotalDuration,
                () => PlayheadPosition,
                pos => PlayheadPosition = pos,
                () => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastSyncedPlayheadBits)),
                val => Interlocked.Exchange(ref _lastSyncedPlayheadBits, BitConverter.DoubleToInt64Bits(val)),
                val => IsPlaying = val,
                (pos, forceResync) => _audioPreviewService.SyncPreviewAudio(pos, forceResync));
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

        // OnPlaybackStarted is now handled by TimelinePlaybackCoordinator which fires
        // _updateSegmentAudioPlayback on its PlaybackStarted handler via the WpfPlaybackDispatcher.

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
                // Always use the maximum of the two so segments can always extend freely.
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

                // Push PixelsPerSecond to all segments for binding-based layout
                BroadcastPixelsPerSecond();
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

            var assets = _projectViewModel?.CurrentProject?.Assets;
            if (assets == null || assets.Count == 0)
            {
                Log.Warning("EnqueueSegmentPeakLoad: Assets collection is null/empty for segment {SegmentId} — waveform peaks will not load. " +
                    "This may indicate CurrentProject was replaced with a partially-loaded entity.",
                    segment.Id);
                return;
            }

            var asset = assets.FirstOrDefault(a => a.Id == segment.BackgroundAssetId);

            if (asset == null || string.IsNullOrEmpty(asset.FilePath)
                || !string.Equals(asset.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("EnqueueSegmentPeakLoad: No matching audio asset for segment {SegmentId}, assetId={AssetId}",
                    segment.Id, segment.BackgroundAssetId);
                return;
            }

            var path = asset.FilePath;
            _ = Task.Run(async () =>
            {
                await _peakLoadThrottle.WaitAsync();
                try
                {
                    var (peaks, actualDuration) = AudioService.GetPeakSamplesFromFile(path);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        segment.WaveformPeaks = peaks;
                        // Use sample-accurate duration (not metadata) for correct waveform slicing
                        segment.SourceFileDuration = actualDuration > 0 ? actualDuration : (asset.Duration ?? 0);
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load waveform peaks for segment {SegmentId}, file: {FilePath}",
                        segment.Id, path);
                }
                finally
                {
                    _peakLoadThrottle.Release();
                }
            });
        }

        /// <summary>
        /// Re-enqueue waveform peak loading for any audio segments that have null or empty peaks.
        /// Call after the visual tree is recreated (e.g. tab switch) to restore waveform displays
        /// even when <see cref="LoadTracksFromProject"/> is not re-triggered.
        /// </summary>
        public void RefreshWaveformPeaks()
        {
            var hasAudioSegments = false;
            foreach (var track in Tracks)
            {
                foreach (var seg in track.Segments)
                {
                    if (seg.WaveformPeaks == null || (seg.WaveformPeaks is float[] arr && arr.Length == 0))
                    {
                        hasAudioSegments |= !string.IsNullOrEmpty(seg.BackgroundAssetId);
                        EnqueueSegmentPeakLoad(seg);
                    }
                }
            }

            if (hasAudioSegments)
                Log.Debug("RefreshWaveformPeaks: Re-enqueued peak loads for audio segments with missing waveforms");
        }

        /// <summary>
        /// Calculate pixels per second based on timeline width and total duration.
        /// </summary>
        private void RecalculatePixelsPerSecond()
        {
            if (TotalDuration <= 0)
                TotalDuration = TimelineConstants.DefaultEmptyDuration;

            PixelsPerSecond = _layoutService.CalculatePixelsPerSecond(TimelineWidth, TotalDuration);
        }

        /// <summary>
        /// Push PixelsPerSecond to all segments so their PixelLeft/PixelWidth update via binding.
        /// </summary>
        private void BroadcastPixelsPerSecond()
        {
            double pps = PixelsPerSecond;
            foreach (var track in Tracks)
                foreach (var seg in track.Segments)
                    seg.TimelinePixelsPerSecond = pps;
        }

        /// <summary>Called by source generator when PixelsPerSecond changes.</summary>
        partial void OnPixelsPerSecondChanged(double value)
        {
            BroadcastPixelsPerSecond();
        }

        /// <summary>
        /// Public wrapper so the View can trigger a full recalculation after drag completes.
        /// </summary>
        public void RecalculatePixelsPerSecondPublic() => RecalculatePixelsPerSecond();

        /// <summary>
        /// Returns the latest EndTime across all segments on all tracks, or 0 if none.
        /// Used to determine timeline duration when no main audio file is loaded.
        /// </summary>
        public double ComputeMaxSegmentEndTime()
        {
            return _layoutService.ComputeMaxSegmentEndTime(Tracks);
        }

        /// <summary>
        /// Recalculate <see cref="TotalDuration"/> from segment data, always keeping the timeline
        /// at least as long as any audio loaded in the player. Falls back to 60 s when empty.
        /// </summary>
        public void RecalculateDurationFromSegments()
        {
            var result = _layoutService.RecalculateDuration(Tracks, _audioService.GetDuration(), TimelineWidth);
            TotalDuration = result.totalDuration;
            PixelsPerSecond = result.pixelsPerSecond;
            TimelineWidth = result.timelineWidth;
        }

        /// <summary>
        /// True when a main audio file is loaded (defines the timeline boundary).
        /// When false, segment durations define TotalDuration and movement is unconstrained rightward.
        /// </summary>
        public bool IsMainAudioLoaded => _audioService.GetDuration() > 0;

        /// <summary>
        /// Expand the timeline when a segment is moved/resized past the current right edge.
        /// Does NOT recalculate PixelsPerSecond so active drags remain stable.
        /// Capped to prevent runaway expansion during drag interactions.
        /// </summary>
        public void ExpandTimelineToFit(double segmentEndTime)
        {
            if (segmentEndTime > TotalDuration)
            {
                // Cap: don't expand more than 4× the buffer per call.
                // This prevents infinite timeline growth if collision resolution
                // repeatedly pushes segments rightward.
                double maxExpansion = TotalDuration + TimelineConstants.SegmentEndBuffer * 4;
                double proposed = segmentEndTime + TimelineConstants.SegmentEndBuffer;
                TotalDuration = Math.Min(proposed, maxExpansion);
            }
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
            return _snapService.HasOverlap(s1.StartTime, s1.EndTime, s2.StartTime, s2.EndTime);
        }

        /// <summary>
        /// Check collision for a segment within a specific track.
        /// </summary>
        private bool CheckCollisionInTrack(Segment segment, string trackId, Segment? excludeSegment = null)
        {
            // Fast path: use binary-search index when available and clean
            if (!_segmentIndexDirty && _segmentIndexMap != null
                && _segmentIndexMap.TryGetValue(trackId, out var index))
            {
                return index.HasOverlap(segment.StartTime, segment.EndTime, excludeSegment?.Id);
            }

            // Fallback: linear scan
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
            return _snapService.SnapToGrid(timeSeconds, GridSize);
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
        /// Magnetic snap: if <paramref name="proposedTime"/> is within <paramref name="thresholdSeconds"/>
        /// of any segment edge in the same track (excluding <paramref name="excludeSegmentId"/>),
        /// return the snapped edge time; otherwise return <paramref name="proposedTime"/>.
        /// </summary>
        public double SnapToSegmentEdge(double proposedTime, string? trackId, string? excludeSegmentId, double thresholdSeconds)
        {
            if (trackId == null) return proposedTime;
            var track = Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track == null) return proposedTime;
            return _snapService.SnapToSegmentEdge(proposedTime, track.Segments, excludeSegmentId, thresholdSeconds);
        }

        /// <summary>
        /// Cross-track magnetic snap: snap to nearest segment edge across ALL tracks
        /// except the specified track (where collision resolution handles alignment).
        /// Used for resize operations to show guide lines only for cross-track alignment.
        /// </summary>
        public double SnapToCrossTrackEdge(double proposedTime, string? excludeTrackId, string? excludeSegmentId, double thresholdSeconds)
        {
            return _snapService.SnapToCrossTrackEdge(proposedTime, Tracks, excludeTrackId, excludeSegmentId, thresholdSeconds);
        }

        /// <summary>
        /// All-track magnetic snap: snap to nearest segment edge across ALL tracks.
        /// Used for move operations to align with any track's segment edges.
        /// </summary>
        public double SnapToAllTrackEdges(double proposedTime, string? excludeSegmentId, double thresholdSeconds)
        {
            return _snapService.SnapToAllTrackEdges(proposedTime, Tracks, excludeSegmentId, thresholdSeconds);
        }

        /// <summary>
        /// Update a segment's timing with collision resolution and grid snapping.
        /// Returns true if the timing was successfully updated.
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

                var resolved = _snapService.ResolveTiming(
                    segment, newStartTime, newEndTime,
                    track.Segments, TotalDuration, GridSize,
                    ExpandTimelineToFit);

                if (!resolved.HasValue)
                {
                    StatusMessage = "Cannot move: overlaps with other segment in track";
                    return false;
                }

                segment.StartTime = resolved.Value.start;
                segment.EndTime = resolved.Value.end;

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
            if (_cachedActiveSegments != null && Math.Abs(timeSeconds - _cachedActiveSegmentsTime) < 0.0005)
                return _cachedActiveSegments;

            // Rebuild per-track interval index if dirty
            if (_segmentIndexDirty || _segmentIndexMap == null)
            {
                _segmentIndexMap = new Dictionary<string, SegmentIntervalIndex>(Tracks.Count);
                foreach (var t in Tracks)
                {
                    if (t.Segments != null && t.Id != null)
                        _segmentIndexMap[t.Id] = new SegmentIntervalIndex(t.Segments);
                }
                _segmentIndexDirty = false;
            }

            var result = new List<(Track track, Segment segment)>(4);

            foreach (var track in Tracks)
            {
                if (track.Segments == null || !track.IsVisible)
                    continue;

                // Use binary-search index when available, fall back to linear scan
                if (track.Id != null && _segmentIndexMap.TryGetValue(track.Id, out var index))
                {
                    var seg = index.FindAt(timeSeconds);
                    if (seg != null)
                        result.Add((track, seg));
                }
                else
                {
                    foreach (var s in track.Segments)
                    {
                        if (s.StartTime <= timeSeconds && timeSeconds < s.EndTime)
                        {
                            result.Add((track, s));
                            break;
                        }
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
        private void InvalidateActiveSegmentsCache()
        {
            _cachedActiveSegments = null;
            _segmentIndexDirty = true;
        }

        /// <summary>Public wrapper used by TimelineView drag-undo callbacks.</summary>
        public void InvalidateActiveSegmentsCachePublic() => InvalidateActiveSegmentsCache();


        /// <summary>
        /// Handle timeline width change (from UI resize or zoom).
        /// </summary>
        partial void OnTimelineWidthChanged(double value)
        {
            if (!IsDeferringThumbnailUpdate)
                RecalculatePixelsPerSecond();
            OnPropertyChanged(nameof(TimelineContentWidth));
        }

        /// <summary>
        /// Handle total duration change.
        /// Skip recalculation during an active segment drag so that the pixel↔time
        /// conversion rate stays constant and the segment doesn't oscillate.
        /// </summary>
        partial void OnTotalDurationChanged(double value)
        {
            if (!IsDeferringThumbnailUpdate)
                RecalculatePixelsPerSecond();
        }

        public void Dispose()
        {
            _projectViewModel.PropertyChanged -= OnProjectViewModelPropertyChanged;
            if (_selectionSyncService != null)
                _selectionSyncService.CanvasSelectionChanged -= OnCanvasElementSelected;
            _playbackCoordinator.Dispose();
            _audioService.StopSegmentAudio();
        }
    }

}
