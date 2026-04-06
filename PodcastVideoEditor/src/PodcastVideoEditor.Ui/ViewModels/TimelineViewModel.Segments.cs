#nullable enable
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    // ── Segment CRUD, multi-select, and background management ──

    public partial class TimelineViewModel
    {
        // ── Multi-select state (delegated to TimelineSelectionService) ───────────

        private readonly TimelineSelectionService _selectionService = new();

        /// <summary>True when one or more segments are in the multi-selection.</summary>
        public bool HasMultiSelection => _selectionService.HasSelection;

        /// <summary>Number of multi-selected segments.</summary>
        public int MultiSelectionCount => _selectionService.Count;

        /// <summary>Read-only access to multi-selected segments.</summary>
        public IReadOnlyCollection<Segment> MultiSelectedSegments => _selectionService.SelectedSegments;

        /// <summary>The underlying selection service (for rubber-band from View).</summary>
        public TimelineSelectionService SelectionService => _selectionService;

        /// <summary>
        /// Toggle <paramref name="segment"/> in/out of the multi-selection (Ctrl+Click).
        /// </summary>
        public void ToggleMultiSelect(Segment segment)
        {
            _selectionService.Toggle(segment);
            StatusMessage = _selectionService.HasSelection
                ? $"{_selectionService.Count} segment(s) selected"
                : "Multi-selection cleared";
        }

        /// <summary>
        /// Range-select from anchor to target (Shift+Click).
        /// </summary>
        public void RangeMultiSelect(Segment segment)
        {
            _selectionService.RangeSelect(segment, Tracks);
            StatusMessage = _selectionService.HasSelection
                ? $"{_selectionService.Count} segment(s) selected"
                : "Multi-selection cleared";
        }

        /// <summary>Clear all multi-selected segments without deleting them.</summary>
        public void ClearMultiSelection()
        {
            _selectionService.Clear();
        }

        // ── Segment add / create ────────────────────────────────────────────────

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

                var newSegment = BuildSegment(
                    targetTrack,
                    SnapToGrid(PlayheadPosition),
                    SnapToGrid(endTime),
                    SegmentKinds.Visual,
                    "New Segment");

                string msg = targetTrack != SelectedTrack ? "Segment added to Visual 1" : "Segment added";
                if (!CommitSegmentToTrack(targetTrack, newSegment, msg))
                    return;

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

                var newSegment = BuildSegment(
                    targetTrack,
                    SnapToGrid(PlayheadPosition),
                    SnapToGrid(endTime),
                    segmentKind,
                    asset.Name ?? "Segment",
                    asset.Id);

                if (!CommitSegmentToTrack(targetTrack, newSegment, "Segment added with media"))
                    return;

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
        /// Unified pipeline: auto-expand timeline, collision check, add to track, invalidate,
        /// select, enqueue waveform peaks, record undo. Returns true on success.
        /// </summary>
        private bool CommitSegmentToTrack(Track track, Segment segment, string? successMessage = null)
        {
            // Auto-expand timeline if segment exceeds current end
            if (segment.EndTime > TotalDuration)
                ExpandTimelineToFit(segment.EndTime);

            if (segment.EndTime <= segment.StartTime)
            {
                StatusMessage = "No room for segment (zero duration)";
                return false;
            }

            if (CheckCollisionInTrack(segment, track.Id))
            {
                StatusMessage = "Segment overlaps with existing segment in this track";
                return false;
            }

            track.Segments.Add(segment);
            segment.TimelinePixelsPerSecond = PixelsPerSecond;
            InvalidateActiveSegmentsCache();
            SelectSegment(segment);
            EnqueueSegmentPeakLoad(segment);
            _undoRedo?.Record(new SegmentAddedAction(
                (System.Collections.ObjectModel.ObservableCollection<Segment>)track.Segments,
                segment, InvalidateActiveSegmentsCache, seg => { if (seg != null) SelectSegment(seg); else { SelectedSegment = null; } }));
            StatusMessage = successMessage ?? "Segment added";
            return true;
        }

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
                string kind = track.TrackType?.ToLowerInvariant() switch
                {
                    "audio"  => SegmentKinds.Audio,
                    "effect" => SegmentKinds.Effect,
                    "text"   => SegmentKinds.Text,
                    _        => SegmentKinds.Visual
                };

                // Determine duration: use asset duration if available, else default 5s
                double duration = asset.Duration > 0 ? asset.Duration.Value : 5.0;
                double startTime = SnapToGrid(Math.Max(0, timeSeconds));
                double endTime = SnapToGrid(startTime + duration);

                var newSegment = BuildSegment(
                    track,
                    startTime,
                    endTime,
                    kind,
                    asset.Name ?? "Segment",
                    asset.Id);

                if (!CommitSegmentToTrack(track, newSegment, $"Dropped '{asset.Name}' at {startTime:F2}s"))
                    return false;

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
        /// Add a segment at a position, auto-creating a new track if collision occurs.
        /// This is the preferred drop method for commercial-grade UX: if the target track
        /// already has a segment at the drop position, a new track of the same type is
        /// inserted just above the target track and the segment is placed there instead.
        /// Returns the track the segment was actually added to (or null on failure).
        /// </summary>
        public Track? AddSegmentWithAutoTrack(Track track, double timeSeconds, Asset asset)
        {
            if (_projectViewModel.CurrentProject == null || track == null || asset == null)
                return null;

            try
            {
                string kind = track.TrackType?.ToLowerInvariant() switch
                {
                    "audio" => SegmentKinds.Audio,
                    "effect" => SegmentKinds.Effect,
                    "text" => SegmentKinds.Text,
                    _ => SegmentKinds.Visual
                };

                double duration = asset.Duration > 0 ? asset.Duration.Value : 5.0;
                double startTime = SnapToGrid(Math.Max(0, timeSeconds));
                double endTime = SnapToGrid(startTime + duration);

                var newSegment = BuildSegment(track, startTime, endTime, kind, asset.Name ?? "Segment", asset.Id);

                // Try target track first
                if (CommitSegmentToTrack(track, newSegment, $"Dropped '{asset.Name}' at {startTime:F2}s"))
                {
                    Log.Information("Segment dropped: asset {AssetId} at {StartTime}s on track {TrackId}", asset.Id, startTime, track.Id);
                    return track;
                }

                // Collision detected — auto-create a new track above the target
                var trackType = track.TrackType ?? TrackTypes.Visual;
                var newTrack = InsertTrackAt(track.Order, trackType);
                if (newTrack == null)
                {
                    StatusMessage = "Failed to create new track for drop";
                    return null;
                }

                // Rebuild the segment for the new track
                newSegment = BuildSegment(newTrack, startTime, endTime, kind, asset.Name ?? "Segment", asset.Id);
                if (CommitSegmentToTrack(newTrack, newSegment, $"Dropped '{asset.Name}' on new track at {startTime:F2}s"))
                {
                    Log.Information("Segment auto-dropped: asset {AssetId} at {StartTime}s on auto-created track {TrackId}",
                        asset.Id, startTime, newTrack.Id);
                    return newTrack;
                }

                StatusMessage = "Failed to add segment even on new track";
                return null;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error dropping asset: {ex.Message}";
                Log.Error(ex, "Error adding segment from drag-drop");
                return null;
            }
        }

        /// <summary>
        /// Check if dropping a segment at the given time on a track would cause a collision.
        /// Used by view layer for ghost preview visual feedback.
        /// </summary>
        public bool WouldCollideOnDrop(Track track, double timeSeconds, double duration)
        {
            if (track == null) return false;
            double startTime = SnapToGrid(Math.Max(0, timeSeconds));
            double endTime = SnapToGrid(startTime + duration);
            // Create a temporary segment to check collision
            var tempSegment = new Segment { StartTime = startTime, EndTime = endTime };
            return CheckCollisionInTrack(tempSegment, track.Id);
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
                TrackTypes.Effect => false, // Effect tracks are for visualizer elements only
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

            string kind = trackType.ToLowerInvariant() switch
            {
                TrackTypes.Audio  => SegmentKinds.Audio,
                TrackTypes.Effect => SegmentKinds.Effect,
                TrackTypes.Text   => SegmentKinds.Text,
                _                 => SegmentKinds.Visual
            };
            var newSegment = BuildSegment(targetTrack, startTime, endTime, kind, text);

            if (!CommitSegmentToTrack(targetTrack, newSegment, $"Created element segment"))
                return null;

            Log.Information("Created element segment '{Text}' on {TrackType} track at {Start}s-{End}s", text, trackType, startTime, endTime);
            return newSegment;
        }

        /// <summary>
        /// Create a segment at a specific time position (for drag-drop from element panel).
        /// When <paramref name="trackId"/> is specified the segment is placed on that exact track;
        /// otherwise the first matching track type is used (or a new one is created).
        /// </summary>
        public Segment? CreateSegmentForElementAtTime(string trackType, string text, double startTime, double duration = 5.0, string? trackId = null)
        {
            if (_projectViewModel?.CurrentProject == null)
                return null;

            Track? targetTrack = null;
            if (!string.IsNullOrEmpty(trackId))
                targetTrack = Tracks.FirstOrDefault(t => t.Id == trackId);

            if (targetTrack == null)
            {
                targetTrack = Tracks.FirstOrDefault(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase));
                if (targetTrack == null)
                {
                    AddTrack(trackType);
                    targetTrack = Tracks.LastOrDefault(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase));
                    if (targetTrack == null)
                        return null;
                }
            }

            double snappedStart = SnapToGrid(Math.Max(0, startTime));
            double snappedEnd = SnapToGrid(snappedStart + duration);

            string kind = trackType.ToLowerInvariant() switch
            {
                TrackTypes.Audio  => SegmentKinds.Audio,
                TrackTypes.Effect => SegmentKinds.Effect,
                TrackTypes.Text   => SegmentKinds.Text,
                _                 => SegmentKinds.Visual
            };
            var newSegment = BuildSegment(targetTrack, snappedStart, snappedEnd, kind, text);

            if (CommitSegmentToTrack(targetTrack, newSegment, $"Dropped element at {snappedStart:F2}s"))
            {
                Log.Information("Created element segment '{Text}' on {TrackType} track at {Start}s-{End}s (drag-drop)", text, trackType, snappedStart, snappedEnd);
                return newSegment;
            }

            // Collision — auto-create a new track above target
            var autoTrack = InsertTrackAt(targetTrack.Order, targetTrack.TrackType ?? trackType);
            if (autoTrack == null)
                return null;

            newSegment = BuildSegment(autoTrack, snappedStart, snappedEnd, kind, text);
            if (!CommitSegmentToTrack(autoTrack, newSegment, $"Dropped element on new track at {snappedStart:F2}s"))
                return null;

            Log.Information("Created element segment '{Text}' on auto-created {TrackType} track at {Start}s-{End}s (drag-drop)", text, trackType, snappedStart, snappedEnd);
            return newSegment;
        }

        /// <summary>
        /// Create a segment on a BRAND-NEW dedicated track of the given type.
        /// Used by VisualizerElement (and similar overlays) so they never conflict
        /// with existing segments on shared visual tracks.
        /// </summary>
        public Segment? CreateSegmentOnNewTrack(string trackType, string text, double duration = 5.0)
        {
            if (_projectViewModel?.CurrentProject == null)
                return null;

            AddTrack(trackType, name: text);
            var newTrack = Tracks.LastOrDefault(t => string.Equals(t.TrackType, trackType, StringComparison.OrdinalIgnoreCase));
            if (newTrack == null)
                return null;

            double startTime = SnapToGrid(PlayheadPosition);
            double endTime   = SnapToGrid(startTime + duration);

            string kind = trackType.ToLowerInvariant() switch
            {
                TrackTypes.Audio  => SegmentKinds.Audio,
                TrackTypes.Effect => SegmentKinds.Effect,
                TrackTypes.Text   => SegmentKinds.Text,
                _                 => SegmentKinds.Visual
            };
            var newSegment = BuildSegment(newTrack, startTime, endTime, kind, text);
            if (!CommitSegmentToTrack(newTrack, newSegment, $"Created element segment on new track"))
                return null;

            Log.Information("Created element segment '{Text}' on NEW {TrackType} track at {Start}s-{End}s",
                text, trackType, startTime, endTime);
            return newSegment;
        }

        /// <summary>
        /// Create a full-span overlay segment on a NEW track. The segment starts at 0
        /// and stretches to the end of the project (max of all existing segments / audio).
        /// Ideal for logos, watermarks, and other elements that should persist across
        /// the entire video. The asset is linked via <see cref="Segment.BackgroundAssetId"/>.
        /// </summary>
        public Segment? CreateFullSpanOverlaySegment(string trackType, string text, string? assetId = null)
        {
            if (_projectViewModel?.CurrentProject == null)
                return null;

            double projectEnd = GetProjectDuration();

            // Insert at the top (Order 0) so overlays render above existing content
            var newTrack = InsertTrackAt(0, trackType, name: text);
            if (newTrack == null)
                return null;

            double startTime = 0.0;
            double endTime = SnapToGrid(projectEnd);

            string kind = trackType.ToLowerInvariant() switch
            {
                TrackTypes.Audio  => SegmentKinds.Audio,
                TrackTypes.Effect => SegmentKinds.Effect,
                TrackTypes.Text   => SegmentKinds.Text,
                _                 => SegmentKinds.Visual
            };
            var newSegment = BuildSegment(newTrack, startTime, endTime, kind, text, assetId);
            if (!CommitSegmentToTrack(newTrack, newSegment, $"Created full-span overlay on new track"))
                return null;

            Log.Information("Created full-span overlay '{Text}' on NEW {TrackType} track (0s-{End}s, asset={AssetId})",
                text, trackType, endTime, assetId ?? "(none)");
            return newSegment;
        }

        /// <summary>
        /// Returns the total duration of the current project in seconds.
        /// Computed as the maximum of all track-segment end times and the project audio file length.
        /// Falls back to a 30-second minimum so a freshly-created project yields a sensible range.
        /// Used by <see cref="PodcastVideoEditor.Ui.ViewModels.CanvasViewModel"/> when sizing the
        /// visualizer timeline segment to span the full project instead of the 5-second stub default.
        /// </summary>
        public double GetProjectDuration()
        {
            var trackMax = Tracks
                .SelectMany(t => t.Segments)
                .Select(s => s.EndTime)
                .DefaultIfEmpty(0.0)
                .Max();

            double audioMax = 0.0;
            var audioPath = _projectViewModel?.CurrentProject?.AudioPath;
            if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
            {
                try
                {
                    using var reader = new NAudio.Wave.AudioFileReader(audioPath);
                    audioMax = reader.TotalTime.TotalSeconds;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "GetProjectDuration: could not read audio file length, using track max");
                }
            }

            return Math.Max(30.0, Math.Max(trackMax, audioMax));
        }

        // ── Segment clear / delete / remove ─────────────────────────────────────

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
        /// Delete selected segment (or all multi-selected segments if any are selected).
        /// </summary>
        [RelayCommand]
        public void DeleteSelectedSegment()
        {
            try
            {
                // Multi-select delete takes priority
                if (_selectionService.HasSelection)
                {
                    int totalCount = _selectionService.Count;
                    // Skip segments whose track is locked
                    var toDelete = _selectionService.SelectedSegments
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
        /// Remove a segment by its ID without recording an undo action.
        /// Returns the <see cref="SegmentDeletedAction"/> so the caller can compound it.
        /// Returns null if the segment was not found.
        /// </summary>
        public IUndoableAction? RemoveSegmentById(string segmentId)
        {
            foreach (var track in Tracks)
            {
                var segs = track.Segments as ObservableCollection<Segment>;
                if (segs == null) continue;
                var seg = segs.FirstOrDefault(s => s.Id == segmentId);
                if (seg == null) continue;

                int idx = segs.IndexOf(seg);
                segs.Remove(seg);
                InvalidateActiveSegmentsCache();
                if (SelectedSegment == seg) SelectedSegment = null;

                return new SegmentDeletedAction(
                    segs, seg, idx,
                    InvalidateActiveSegmentsCache,
                    s => { if (s != null) SelectSegment(s); else SelectedSegment = null; });
            }
            return null;
        }

        /// <summary>
        /// Move a segment from its current track to a different track during a drag operation.
        /// Does NOT record undo (the calling drag handler records timing undo on CompleteDrag).
        /// Returns true if the segment was successfully moved to the target track.
        /// </summary>
        public bool MoveSegmentToTrack(Segment segment, Track targetTrack)
        {
            if (segment == null || targetTrack == null) return false;
            if (segment.TrackId == targetTrack.Id) return false;
            if (targetTrack.IsLocked) return false;

            // Type compatibility: segment kind must match target track type
            if (!string.Equals(segment.Kind, targetTrack.TrackType, StringComparison.OrdinalIgnoreCase))
                return false;

            // Check collision on target track — if collision, auto-create a new track
            Track actualTarget = targetTrack;
            if (CheckCollisionInTrack(segment, targetTrack.Id, segment))
            {
                var autoTrack = InsertTrackAt(targetTrack.Order, targetTrack.TrackType ?? segment.Kind);
                if (autoTrack == null)
                    return false;
                actualTarget = autoTrack;
            }

            // Remove from source track
            var sourceTrack = Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
            if (sourceTrack?.Segments is System.Collections.ObjectModel.ObservableCollection<Segment> sourceSegs)
                sourceSegs.Remove(segment);

            // Add to target track — update BOTH FK and navigation property.
            // If segment.Track stays stale, EF Core relationship fixup during autosave
            // will use the old navigation to overwrite TrackId, reverting the move.
            segment.TrackId = actualTarget.Id;
            segment.Track = actualTarget;
            if (actualTarget.Segments is System.Collections.ObjectModel.ObservableCollection<Segment> targetSegs)
                targetSegs.Add(segment);

            InvalidateActiveSegmentsCache();
            return true;
        }

        // ── Segment select / duplicate / split ──────────────────────────────────

        /// <summary>
        /// Select all segments across all tracks (Ctrl+A).
        /// </summary>
        [RelayCommand]
        public void SelectAllSegments()
        {
            _selectionService.SelectAll(Tracks);
            int total = _selectionService.Count;
            StatusMessage = total > 0 ? $"All {total} segment(s) selected" : "No segments to select";
        }

        /// <summary>
        /// Duplicate selected segment(s) with offset, in the same track.
        /// Supports multi-selection: all selected segments are duplicated.
        /// </summary>
        [RelayCommand]
        public void DuplicateSelectedSegment()
        {
            try
            {
                // Multi-select duplicate
                if (_selectionService.HasSelection)
                {
                    var toDuplicate = _selectionService.SelectedSegments
                        .Where(seg => Tracks.FirstOrDefault(t => t.Id == seg.TrackId)?.IsLocked != true)
                        .OrderBy(seg => seg.StartTime)
                        .ToList();

                    if (toDuplicate.Count == 0) { StatusMessage = "All selected segments are on locked tracks"; return; }

                    var actions = new List<IUndoableAction>();
                    foreach (var original in toDuplicate)
                    {
                        var track = Tracks.FirstOrDefault(t => t.Id == original.TrackId);
                        if (track == null) continue;

                        double duration = original.EndTime - original.StartTime;
                        var duplicate = CloneSegmentWithOffset(original, track, duration);

                        if (track.Segments is ObservableCollection<Segment> segs)
                        {
                            if (CheckCollisionInTrack(duplicate, track.Id)) continue;
                            segs.Add(duplicate);
                            duplicate.TimelinePixelsPerSecond = PixelsPerSecond;
                            actions.Add(new SegmentAddedAction(
                                segs, duplicate, InvalidateActiveSegmentsCache,
                                seg => { if (seg != null) SelectSegment(seg); else SelectedSegment = null; }));
                        }
                    }

                    if (actions.Count > 0)
                        _undoRedo?.Record(new CompoundAction($"Duplicate {actions.Count} segment(s)", actions));
                    InvalidateActiveSegmentsCache();
                    StatusMessage = $"{actions.Count} segment(s) duplicated";
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

                var orig = SelectedSegment;
                double dur = orig.EndTime - orig.StartTime;
                var dup = CloneSegmentWithOffset(orig, SelectedTrack, dur);

                if (!CommitSegmentToTrack(SelectedTrack, dup, "Segment duplicated"))
                    return;

                Log.Information("Segment duplicated in track {TrackId}", SelectedTrack.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error duplicating segment: {ex.Message}";
                Log.Error(ex, "Error duplicating segment");
            }
        }

        private Segment CloneSegmentWithOffset(Segment original, Track track, double duration)
        {
            return new Segment
            {
                ProjectId = original.ProjectId,
                TrackId = original.TrackId,
                StartTime = SnapToGrid(original.EndTime + TimelineConstants.DuplicateGapSeconds),
                EndTime = SnapToGrid(original.EndTime + TimelineConstants.DuplicateGapSeconds + duration),
                Text = original.Text + " (Copy)",
                Kind = original.Kind,
                BackgroundAssetId = original.BackgroundAssetId,
                TransitionType = original.TransitionType,
                TransitionDuration = original.TransitionDuration,
                Volume = original.Volume,
                FadeInDuration = original.FadeInDuration,
                FadeOutDuration = original.FadeOutDuration,
                SourceStartOffset = original.SourceStartOffset,
                Order = track.Segments.Count
            };
        }

        /// <summary>
        /// Split the selected segment(s) at the current playhead position (Ctrl+B).
        /// Multi-select: splits ALL selected segments intersected by playhead.
        /// Creates two segments per split: [original.Start → playhead] and [playhead → original.End].
        /// </summary>
        [RelayCommand]
        public void SplitSelectedSegmentAtPlayhead()
        {
            try
            {
                var splitTime = SnapToGrid(PlayheadPosition);
                const double margin = 0.05; // 50ms minimum segment duration

                // Multi-split: split all selected segments at playhead
                if (_selectionService.HasSelection)
                {
                    var toSplit = _selectionService.SelectedSegments
                        .Where(seg =>
                        {
                            var track = Tracks.FirstOrDefault(t => t.Id == seg.TrackId);
                            return track?.IsLocked != true
                                && splitTime > seg.StartTime + margin
                                && splitTime < seg.EndTime - margin;
                        })
                        .ToList();

                    if (toSplit.Count == 0) { StatusMessage = "No selected segments intersect the playhead"; return; }

                    ClearMultiSelection();
                    var actions = new List<IUndoableAction>();
                    foreach (var segment in toSplit)
                    {
                        var track = Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
                        if (track?.Segments is not ObservableCollection<Segment> segs) continue;

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
                            FadeInDuration = 0,
                            FadeOutDuration = segment.FadeOutDuration,
                            Order = segs.Count
                        };
                        double origEnd = segment.EndTime;
                        segment.EndTime = splitTime;
                        segment.FadeOutDuration = 0;
                        segs.Add(rightHalf);
                        rightHalf.TimelinePixelsPerSecond = PixelsPerSecond;
                        actions.Add(new SegmentSplitAction(
                            segs, segment, origEnd, rightHalf,
                            InvalidateActiveSegmentsCache,
                            seg2 => { if (seg2 != null) SelectSegment(seg2); else SelectedSegment = null; }));
                    }
                    if (actions.Count > 0)
                        _undoRedo?.Record(new CompoundAction($"Split {actions.Count} segment(s)", actions));
                    InvalidateActiveSegmentsCache();
                    StatusMessage = $"{actions.Count} segment(s) split at {splitTime:F2}s";
                    return;
                }

                // Single-segment fallback
                if (SelectedSegment == null) { StatusMessage = "No segment selected"; return; }
                if (SelectedTrack == null) { StatusMessage = "No track context"; return; }

                var singleSeg = SelectedSegment;
                if (splitTime <= singleSeg.StartTime + margin || splitTime >= singleSeg.EndTime - margin)
                {
                    StatusMessage = "Playhead must be inside the segment to split";
                    return;
                }

                var singleRight = new Segment
                {
                    ProjectId = singleSeg.ProjectId,
                    TrackId = singleSeg.TrackId,
                    StartTime = splitTime,
                    EndTime = singleSeg.EndTime,
                    Text = singleSeg.Text,
                    Kind = singleSeg.Kind,
                    BackgroundAssetId = singleSeg.BackgroundAssetId,
                    TransitionType = singleSeg.TransitionType,
                    TransitionDuration = singleSeg.TransitionDuration,
                    Volume = singleSeg.Volume,
                    FadeInDuration = 0,
                    FadeOutDuration = singleSeg.FadeOutDuration,
                    Order = SelectedTrack.Segments.Count
                };
                double origEndSingle = singleSeg.EndTime;
                singleSeg.EndTime = splitTime;
                singleSeg.FadeOutDuration = 0;

                SelectedTrack.Segments.Add(singleRight);
                singleRight.TimelinePixelsPerSecond = PixelsPerSecond;
                InvalidateActiveSegmentsCache();
                SelectSegment(singleRight);
                _undoRedo?.Record(new SegmentSplitAction(
                    (System.Collections.ObjectModel.ObservableCollection<Segment>)SelectedTrack.Segments,
                    singleSeg, origEndSingle, singleRight,
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

        // ── Segment background management (called by SegmentEditorPanel) ───

        /// <summary>
        /// Assign a file as the background asset for the currently selected segment.
        /// Ingests the file as a project asset if not already present.
        /// </summary>
        public async Task<bool> SetSegmentBackgroundAsync(string filePath)
        {
            if (SelectedSegment == null) return false;
            if (!string.Equals(SelectedSegment.Kind, "visual", StringComparison.OrdinalIgnoreCase)) return false;

            var project = _projectViewModel.CurrentProject;
            if (project == null)
            {
                StatusMessage = "No project loaded";
                return false;
            }

            try
            {
                var ext = System.IO.Path.GetExtension(filePath).Trim('.').ToLowerInvariant();
                var assetType = ext switch
                {
                    "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp" => "Image",
                    "mp4" or "mov" or "mkv" or "avi" or "webm" => "Video",
                    _ => "File"
                };

                var asset = await _projectViewModel.AddAssetToCurrentProjectAsync(filePath, assetType);
                if (asset == null) return false;

                SelectedSegment.BackgroundAssetId = asset.Id;
                await _projectViewModel.SaveProjectAsync();
                StatusMessage = $"Background set: {asset.FileName}";
                Serilog.Log.Information("Background asset assigned to segment {SegmentId}: {AssetId}",
                    SelectedSegment.Id, asset.Id);
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error setting background: {ex.Message}";
                Serilog.Log.Error(ex, "Error choosing background for segment {SegmentId}", SelectedSegment.Id);
                return false;
            }
        }

        /// <summary>
        /// Clear the background asset for the currently selected segment.
        /// </summary>
        public async Task ClearSegmentBackgroundAsync()
        {
            if (SelectedSegment == null) return;

            SelectedSegment.BackgroundAssetId = null;
            await _projectViewModel.SaveProjectAsync();
            StatusMessage = "Background cleared";
            Serilog.Log.Information("Background cleared for segment {SegmentId}", SelectedSegment.Id);
        }

        // ── Close gaps ──────────────────────────────────────────────────────────

        /// <summary>
        /// Close all timing gaps on the selected track (or all tracks if none selected).
        /// Extends each segment's EndTime to the next segment's StartTime.
        /// Paired text+visual tracks are synchronized automatically.
        /// Fully undoable.
        /// </summary>
        [RelayCommand]
        public void CloseGapsOnTrack()
        {
            try
            {
                if (_projectViewModel.CurrentProject == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                var allActions = new List<IUndoableAction>();
                var processedTrackIds = new HashSet<string>();
                int totalClosed = 0;

                // Determine which tracks to process
                IEnumerable<Track> targetTracks;
                if (SelectedTrack != null)
                    targetTracks = new[] { SelectedTrack };
                else
                    targetTracks = Tracks;

                foreach (var track in targetTracks)
                {
                    if (processedTrackIds.Contains(track.Id))
                        continue;
                    if (track.IsLocked)
                        continue;

                    var segments = track.Segments?.ToList();
                    if (segments == null || segments.Count < 2)
                        continue;

                    // Try to find paired track (text↔visual from same import have matching timing)
                    var pairedTrack = FindPairedTrack(track);

                    if (pairedTrack != null && !pairedTrack.IsLocked && !processedTrackIds.Contains(pairedTrack.Id))
                    {
                        var pairedSegments = pairedTrack.Segments?.ToList();
                        if (pairedSegments != null && pairedSegments.Count >= 2)
                        {
                            var (changesA, changesB) = CloseGapsService.CloseGapsPaired(segments, pairedSegments);
                            foreach (var c in changesA)
                                allActions.Add(new SegmentTimingChangedAction(c.Segment, c.Segment.StartTime, c.OldEndTime, c.Segment.StartTime, c.NewEndTime, InvalidateActiveSegmentsCache));
                            foreach (var c in changesB)
                                allActions.Add(new SegmentTimingChangedAction(c.Segment, c.Segment.StartTime, c.OldEndTime, c.Segment.StartTime, c.NewEndTime, InvalidateActiveSegmentsCache));
                            totalClosed += changesA.Count + changesB.Count;
                            processedTrackIds.Add(pairedTrack.Id);
                        }
                        else
                        {
                            // Paired track has no matching segments; close independently
                            var changes = CloseGapsService.CloseGaps(segments);
                            foreach (var c in changes)
                                allActions.Add(new SegmentTimingChangedAction(c.Segment, c.Segment.StartTime, c.OldEndTime, c.Segment.StartTime, c.NewEndTime, InvalidateActiveSegmentsCache));
                            totalClosed += changes.Count;
                        }
                    }
                    else
                    {
                        var changes = CloseGapsService.CloseGaps(segments);
                        foreach (var c in changes)
                            allActions.Add(new SegmentTimingChangedAction(c.Segment, c.Segment.StartTime, c.OldEndTime, c.Segment.StartTime, c.NewEndTime, InvalidateActiveSegmentsCache));
                        totalClosed += changes.Count;
                    }

                    processedTrackIds.Add(track.Id);
                }

                if (allActions.Count > 0)
                {
                    InvalidateActiveSegmentsCache();
                    _undoRedo?.Record(new CompoundAction("Close gaps", allActions));
                    RequestProjectSave();
                    StatusMessage = $"Closed {totalClosed} gap(s) across {processedTrackIds.Count} track(s)";
                    Serilog.Log.Information("CloseGaps: closed {Count} gaps on {Tracks} track(s)", totalClosed, processedTrackIds.Count);
                }
                else
                {
                    StatusMessage = "No gaps to close";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error closing gaps: {ex.Message}";
                Serilog.Log.Error(ex, "Error in CloseGapsOnTrack");
            }
        }

        /// <summary>
        /// Find the paired track (text↔visual) for synchronized gap closure.
        /// Returns null if no valid paired track exists.
        /// </summary>
        private Track? FindPairedTrack(Track track)
        {
            string? pairedType = track.TrackType switch
            {
                TrackTypes.Text => TrackTypes.Visual,
                TrackTypes.Visual => TrackTypes.Text,
                _ => null
            };

            if (pairedType == null)
                return null;

            return Tracks.FirstOrDefault(t => t.TrackType == pairedType);
        }

        // ── Segment image replacement ──────────────────────────────────────────

        /// <summary>
        /// Computed property: true if the selected segment is a visual segment with a background image.
        /// Used by UI to enable/disable "Replace Image" menu items.
        /// </summary>
        public bool SelectedSegmentHasBackground =>
            SelectedSegment != null &&
            SelectedSegment.Kind == "visual" &&
            !string.IsNullOrEmpty(SelectedSegment.BackgroundAssetId);

        /// <summary>
        /// Replace the background image of the selected segment.
        /// Opens file picker to select new image.
        /// </summary>
        [RelayCommand]
        public async Task ReplaceSegmentImage()
        {
            try
            {
                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                if (SelectedSegment.Kind != "visual" || string.IsNullOrEmpty(SelectedSegment.BackgroundAssetId))
                {
                    StatusMessage = "Selected segment is not an image";
                    return;
                }

                // Phase 2: Show file picker dialog
                var dialog = new PodcastVideoEditor.Ui.Dialogs.SelectImageDialog
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };

                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedFilePath))
                {
                    // Phase 3: Process and replace image
                    StatusMessage = $"Selected: {System.IO.Path.GetFileName(dialog.SelectedFilePath)} (Phase 3 - processing pending)";
                    Log.Information("Image file selected for replacement: {FilePath}", dialog.SelectedFilePath);
                    
                    // TODO: Implement in Phase 3
                    // - Validate image file
                    // - Process image (resize, compress via ImageAssetIngestService)
                    // - Create Asset object
                    // - Update segment.BackgroundAssetId
                    // - Save project
                    // - Show success toast
                }
                else
                {
                    StatusMessage = "Image selection canceled";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error replacing image: {ex.Message}";
                Log.Error(ex, "Error replacing segment image");
            }
        }

        /// <summary>
        /// Clear the background image from the selected segment.
        /// </summary>
        [RelayCommand]
        public void ClearSegmentBackground()
        {
            try
            {
                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                if (string.IsNullOrEmpty(SelectedSegment.BackgroundAssetId))
                {
                    StatusMessage = "Segment has no background image";
                    return;
                }

                SelectedSegment.BackgroundAssetId = null;
                StatusMessage = "Segment background cleared";
                Log.Information("Segment background cleared for segment {SegmentId}", SelectedSegment.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error clearing background: {ex.Message}";
                Log.Error(ex, "Error clearing segment background");
            }
        }
    }
}
