// CanvasViewModel.Preview.cs — Preview pipeline: attach wiring, change reactions, composition.
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class CanvasViewModel
    {
        // Debounce timer for batch property changes (undo, script apply, etc.)
        private DispatcherTimer? _previewDebounceTimer;
        private const int PreviewDebounceMs = 30; // coalesce changes within 30ms window

        private void OnPreviewDebounceTimerTick(object? sender, EventArgs e)
        {
            _previewDebounceTimer?.Stop();
            UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
        }

        private void ScheduleDebouncedPreviewUpdate()
        {
            if (_previewDebounceTimer == null)
            {
                _previewDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(PreviewDebounceMs)
                };
                _previewDebounceTimer.Tick += OnPreviewDebounceTimerTick;
            }
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Start();
        }

        /// <summary>
        /// Attach project and timeline references so preview can react to playhead changes.
        /// </summary>
        public void AttachProjectAndTimeline(ProjectViewModel projectViewModel, TimelineViewModel timelineViewModel)
        {
            if (_timelineViewModel != null && _timelinePropertyChangedHandler != null)
            {
                _timelineViewModel.PropertyChanged -= _timelinePropertyChangedHandler;
                _timelineViewModel.ScriptApplied -= OnScriptApplied;
                _timelineViewModel.ScrubCompleted -= OnScrubCompleted;
            }

            if (_projectViewModel != null && _projectPropertyChangedHandler != null)
                _projectViewModel.PropertyChanged -= _projectPropertyChangedHandler;

            DetachTrackSubscriptions();

            _projectViewModel = projectViewModel ?? throw new ArgumentNullException(nameof(projectViewModel));
            _timelineViewModel = timelineViewModel ?? throw new ArgumentNullException(nameof(timelineViewModel));

            _timelinePropertyChangedHandler ??= OnTimelinePropertyChanged;
            _projectPropertyChangedHandler ??= OnProjectPropertyChanged;

            _timelineViewModel.PropertyChanged += _timelinePropertyChangedHandler;
            _timelineViewModel.ScriptApplied += OnScriptApplied;
            _timelineViewModel.ScrubCompleted += OnScrubCompleted;
            _projectViewModel.PropertyChanged += _projectPropertyChangedHandler;

            _tracksCollectionChangedHandler ??= OnTracksCollectionChanged;
            _trackPropertyChangedHandler ??= OnTrackPropertyChanged;
            _segmentPropertyChangedHandler ??= OnSegmentPropertyChanged;
            _trackedTracks = _timelineViewModel.Tracks;
            _trackedTracks.CollectionChanged += _tracksCollectionChangedHandler;

            foreach (var track in _trackedTracks)
                track.PropertyChanged += _trackPropertyChangedHandler;

            RebuildSegmentSubscriptions();

            InvalidateAssetLookup();

            UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        /// <summary>
        /// Called when SeekTo() commits a scrub. Resets the throttle clock so the very next
        /// PlayheadPosition change is guaranteed to refresh the canvas, even if it occurs
        /// within the normal 16 ms throttle window.
        /// </summary>
        private void OnScrubCompleted(object? sender, EventArgs e)
        {
            _lastCanvasUpdateTime = DateTime.MinValue;
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
                // ScrubCompleted resets _lastCanvasUpdateTime so the scrub-commit always
                // renders the correct frame regardless of throttle timing.
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
                InvalidateAssetLookup();
                UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
            }
        }

        private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_trackPropertyChangedHandler == null)
                return;

            if (e.OldItems != null)
            {
                foreach (Track track in e.OldItems)
                    track.PropertyChanged -= _trackPropertyChangedHandler;
            }

            if (e.NewItems != null)
            {
                foreach (Track track in e.NewItems)
                    track.PropertyChanged += _trackPropertyChangedHandler;
            }

            RebuildSegmentSubscriptions();

            UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
        }

        private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_timelineViewModel == null)
                return;

            if (e.PropertyName != nameof(Track.ImageLayoutPreset)
                && e.PropertyName != nameof(Track.IsVisible)
                && e.PropertyName != nameof(Track.Order))
            {
                return;
            }

            if (e.PropertyName == nameof(Track.ImageLayoutPreset) && sender is Track changedTrack)
                UpdateLinkedImageElementsForTrack(changedTrack);

            if (e.PropertyName == nameof(Track.Order))
                RefreshElementZIndices();

            _timelineViewModel.InvalidateActiveSegmentsCachePublic();
            UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        /// <summary>
        /// When the track's ImageLayoutPreset changes, reposition any auto-created ImageElements
        /// linked to segments on that track so the canvas preview reflects the new layout.
        /// </summary>
        private void UpdateLinkedImageElementsForTrack(Track track)
        {
            if (track.Segments == null || Elements.Count == 0)
                return;

            var preset = string.IsNullOrWhiteSpace(track.ImageLayoutPreset)
                ? global::PodcastVideoEditor.Core.Models.ImageLayoutPresets.FullFrame
                : track.ImageLayoutPreset;
            var (ex, ey, ew, eh) = global::PodcastVideoEditor.Core.RenderHelper.ComputeImageRect(preset, CanvasWidth, CanvasHeight);

            foreach (var segment in track.Segments)
            {
                var linked = Elements.FirstOrDefault(el =>
                    el is ImageElement && string.Equals(el.SegmentId, segment.Id, StringComparison.Ordinal));
                if (linked is ImageElement imgEl)
                {
                    imgEl.X = ex;
                    imgEl.Y = ey;
                    imgEl.Width = ew;
                    imgEl.Height = eh;
                }
            }
        }

        private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_timelineViewModel == null)
                return;

            if (e.PropertyName == nameof(Segment.BackgroundAssetId))
            {
                ScheduleDebouncedPreviewUpdate();
                return;
            }

            if (e.PropertyName == nameof(Segment.Text))
            {
                // Sync linked canvas TextOverlayElement content when segment text is edited on timeline
                if (sender is Segment editedSeg)
                {
                    var linkedEl = Elements.FirstOrDefault(el =>
                        string.Equals(el.SegmentId, editedSeg.Id, StringComparison.Ordinal));
                    if (linkedEl is TextOverlayElement tov)
                    {
                        tov.Content = editedSeg.Text ?? string.Empty;
                        tov.Name = editedSeg.Text?.Length > 20
                            ? editedSeg.Text[..20] + "…"
                            : editedSeg.Text ?? "Text";
                    }
                }
                ScheduleDebouncedPreviewUpdate();
                return;
            }

            if (e.PropertyName == nameof(Segment.StartTime)
                || e.PropertyName == nameof(Segment.EndTime)
                || e.PropertyName == nameof(Segment.Kind)
                || e.PropertyName == nameof(Segment.TrackId))
            {
                _timelineViewModel.InvalidateActiveSegmentsCachePublic();
                ScheduleDebouncedPreviewUpdate();
            }
        }

        private void RebuildSegmentSubscriptions()
        {
            if (_segmentPropertyChangedHandler == null)
                return;

            if (_trackedSegments != null)
            {
                foreach (var segment in _trackedSegments)
                    segment.PropertyChanged -= _segmentPropertyChangedHandler;
            }

            // Unsubscribe old segment-collection handlers
            DetachSegmentCollectionSubscriptions();

            if (_trackedTracks == null)
            {
                _trackedSegments = null;
                return;
            }

            _trackedSegments = [];
            foreach (var track in _trackedTracks)
            {
                foreach (var segment in track.Segments)
                {
                    if (_trackedSegments.Add(segment))
                        segment.PropertyChanged += _segmentPropertyChangedHandler;
                }

                // Subscribe to Segments.CollectionChanged so we detect segment add/remove
                if (track.Id != null && track.Segments is System.Collections.Specialized.INotifyCollectionChanged ncc)
                {
                    NotifyCollectionChangedEventHandler handler = (s, e) => OnSegmentsCollectionChanged(track, e);
                    ncc.CollectionChanged += handler;
                    _segmentsCollectionHandlers[track.Id] = handler;
                }
            }
        }

        /// <summary>
        /// Detach all segment-collection (add/remove) subscriptions.
        /// </summary>
        private void DetachSegmentCollectionSubscriptions()
        {
            if (_trackedTracks == null || _segmentsCollectionHandlers.Count == 0)
                return;

            foreach (var track in _trackedTracks)
            {
                if (track.Id != null
                    && _segmentsCollectionHandlers.TryGetValue(track.Id, out var handler)
                    && track.Segments is System.Collections.Specialized.INotifyCollectionChanged ncc)
                {
                    ncc.CollectionChanged -= handler;
                }
            }
            _segmentsCollectionHandlers.Clear();
        }

        /// <summary>
        /// Handles segment add/remove on a track. Cleans up orphaned canvas elements
        /// when segments are deleted and rebuilds property-change subscriptions.
        /// On Undo (segment re-added), restores previously cached elements.
        /// </summary>
        private void OnSegmentsCollectionChanged(Track track, NotifyCollectionChangedEventArgs e)
        {
            if (_timelineViewModel == null)
                return;

            // Handle removed segments: clean up canvas elements linked to them
            if (e.OldItems != null)
            {
                var deletedIds = new List<string>();
                foreach (Segment removed in e.OldItems)
                {
                    if (removed.Id != null)
                        deletedIds.Add(removed.Id);
                    // Unsubscribe PropertyChanged
                    if (_segmentPropertyChangedHandler != null)
                        removed.PropertyChanged -= _segmentPropertyChangedHandler;
                    _trackedSegments?.Remove(removed);
                }
                if (deletedIds.Count > 0)
                {
                    var removed = RemoveOrphanedElements(deletedIds);
                    // Cache for undo restoration
                    foreach (var el in removed)
                    {
                        if (el.SegmentId != null)
                        {
                            if (!_orphanedElementCache.TryGetValue(el.SegmentId, out var list))
                            {
                                list = [];
                                _orphanedElementCache[el.SegmentId] = list;
                            }
                            list.Add(el);
                        }
                    }
                }
            }

            // Handle added segments: subscribe to PropertyChanged and restore cached elements (undo)
            if (e.NewItems != null)
            {
                foreach (Segment added in e.NewItems)
                {
                    if (_trackedSegments != null && _trackedSegments.Add(added) && _segmentPropertyChangedHandler != null)
                        added.PropertyChanged += _segmentPropertyChangedHandler;

                    // Restore cached elements from a previous delete (undo scenario)
                    if (added.Id != null && _orphanedElementCache.TryGetValue(added.Id, out var cached))
                    {
                        foreach (var el in cached)
                        {
                            if (!Elements.Contains(el))
                                Elements.Add(el);
                        }
                        _orphanedElementCache.Remove(added.Id);
                        EnsureVisualizerTimer();
                    }
                }
            }

            // Reset: full rebuild
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                RebuildSegmentSubscriptions();
            }

            _timelineViewModel.InvalidateActiveSegmentsCachePublic();
            ScheduleDebouncedPreviewUpdate();
        }

        private void DetachTrackSubscriptions()
        {
            if (_trackedSegments != null && _segmentPropertyChangedHandler != null)
            {
                foreach (var segment in _trackedSegments)
                    segment.PropertyChanged -= _segmentPropertyChangedHandler;
            }

            DetachSegmentCollectionSubscriptions();

            if (_trackedTracks != null && _tracksCollectionChangedHandler != null)
                _trackedTracks.CollectionChanged -= _tracksCollectionChangedHandler;

            if (_trackedTracks != null && _trackPropertyChangedHandler != null)
            {
                foreach (var track in _trackedTracks)
                    track.PropertyChanged -= _trackPropertyChangedHandler;
            }

            _trackedTracks = null;
            _trackedSegments = null;
        }

        private void UpdateActivePreview(double playheadSeconds)
        {
            if (_timelineViewModel == null || _projectViewModel?.CurrentProject == null)
            {
                ClearActivePreview();
                return;
            }

            var active = _timelineViewModel.GetActiveSegmentsAtTime(playheadSeconds);

            // --- Classify active segments by track type ---
            // GetActiveSegmentsAtTime already filters by Track.IsVisible and sorts by Track.Order (0=front)
            var visualPairs = new List<(Track track, Segment segment)>();
            var textPairs = new List<(Track track, Segment segment)>();

            foreach (var pair in active)
            {
                if (string.Equals(pair.track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase))
                    visualPairs.Add(pair);
                else if (string.Equals(pair.track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    textPairs.Add(pair);
                // Effect / Audio segments: not classified here, but their CanvasElements
                // (VisualizerElement etc.) are managed via UpdateElementVisibility below.
            }

            // --- Multi-text composite ---
            // Auto-create interactive TextOverlayElement for any text segment that doesn't have one yet.
            // This ensures text always renders as a draggable/resizable canvas element.
            foreach (var tp in textPairs)
            {
                if (string.IsNullOrWhiteSpace(tp.segment.Text))
                    continue;

                bool hasLinkedElement = Elements.Any(e =>
                    string.Equals(e.SegmentId, tp.segment.Id, StringComparison.Ordinal));
                if (!hasLinkedElement)
                    EnsureTextElementForSegment(tp.segment);
            }

            // --- Multi-visual composite: iterate visual tracks by z-order ---
            // All images are auto-created as ImageElements for unified z-order with other elements.
            foreach (var vp in visualPairs)
            {
                EnsureImageElementForVisualSegment(vp.track, vp.segment);
            }

            var primaryVisualPair = visualPairs.Count > 0 ? visualPairs[0] : default;
            ActiveVisualSegment = primaryVisualPair.segment;

            // --- Video handling: MediaElement for the frontmost video segment ---
            bool videoHandled = false;
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

                            // Compute video z-index from track order for unified compositing
                            SetActiveVisualLayout(primaryVisualPair.track?.ImageLayoutPreset);
                            PrimaryVideoZIndex = ComputeZIndexForTrack(primaryVisualPair.track);
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

            // Determine placeholder visibility
            bool hasAnyVisual = visualPairs.Count > 0 || IsVideoMode ||
                Elements.Any(e => e is ImageElement || e is VisualizerElement);
            IsVisualPlaceholderVisible = !hasAnyVisual;

            UpdateElementVisibility(active);
        }

        /// <summary>
        /// Auto-create an ImageElement for a visual-track segment that has no linked canvas element,
        /// so images participate in the unified z-order alongside VisualizerElements and other elements.
        /// Video assets are skipped (handled by the MediaElement path).
        /// </summary>
        private void EnsureImageElementForVisualSegment(Track track, Segment segment)
        {
            // Already has a linked canvas element
            if (Elements.Any(e => string.Equals(e.SegmentId, segment.Id, StringComparison.Ordinal)))
                return;

            if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                return;

            var asset = FindAssetById(segment.BackgroundAssetId);
            if (asset == null || string.IsNullOrWhiteSpace(asset.FilePath))
                return;

            // Video assets are rendered via MediaElement, not ImageElement
            if (IsVideoAsset(asset))
                return;

            // Audio assets are not visual
            if (string.Equals(asset.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                return;

            var preset = track.ImageLayoutPreset ?? ImageLayoutPresets.FullFrame;
            var (x, y, w, h) = global::PodcastVideoEditor.Core.RenderHelper.ComputeImageRect(preset, CanvasWidth, CanvasHeight);

            var element = new ImageElement
            {
                Name = asset.Name ?? Path.GetFileNameWithoutExtension(asset.FilePath) ?? "Image",
                FilePath = asset.FilePath,
                X = x,
                Y = y,
                Width = w,
                Height = h,
                ZIndex = ComputeZIndexForTrack(track),
                SegmentId = segment.Id
            };

            Elements.Add(element);
            Serilog.Log.Debug("Auto-created ImageElement for unified compositing: segment {SegId}", segment.Id);
        }

        private void SetActiveVisualLayout(string? preset)
        {
            var normalizedPreset = string.IsNullOrWhiteSpace(preset)
                ? ImageLayoutPresets.FullFrame
                : preset;

            var (x, y, width, height) = global::PodcastVideoEditor.Core.RenderHelper.ComputeImageRect(normalizedPreset, CanvasWidth, CanvasHeight);
            ActiveVisualX = x;
            ActiveVisualY = y;
            ActiveVisualWidth = width;
            ActiveVisualHeight = height;
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

        private void ClearActivePreview()
        {
            VideoSource = null;
            IsVideoMode = false;
            ActiveVisualSegment = null;
            IsVisualPlaceholderVisible = true;
        }
    }
}
