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

            if (!CommitSegmentToTrack(targetTrack, newSegment, $"Dropped element at {snappedStart:F2}s"))
                return null;

            Log.Information("Created element segment '{Text}' on {TrackType} track at {Start}s-{End}s (drag-drop)", text, trackType, snappedStart, snappedEnd);
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

        // ── Segment select / duplicate / split ──────────────────────────────────

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
                    Order = SelectedTrack.Segments.Count
                };

                if (!CommitSegmentToTrack(SelectedTrack, duplicate, "Segment duplicated"))
                    return;

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
    }
}
