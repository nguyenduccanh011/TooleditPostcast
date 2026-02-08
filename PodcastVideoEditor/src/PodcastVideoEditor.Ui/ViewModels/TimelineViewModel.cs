#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        private CancellationTokenSource? _playheadSyncCts;
        private bool _isPlayheadSyncing;

        [ObservableProperty]
        private ObservableCollection<Segment> segments = new();

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
        private double gridSize = 0.1; // 100ms grid snapping

        [ObservableProperty]
        private string statusMessage = "Timeline ready";

        public TimelineViewModel(AudioService audioService, ProjectViewModel projectViewModel)
        {
            _audioService = audioService;
            _projectViewModel = projectViewModel;

            // Subscribe to project changes
            _projectViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ProjectViewModel.CurrentProject))
                {
                    LoadSegmentsFromProject();
                }
            };

            // Start playhead sync loop
            StartPlayheadSync();
        }

        /// <summary>
        /// Load segments from current project.
        /// </summary>
        private void LoadSegmentsFromProject()
        {
            try
            {
                Segments.Clear();

                if (_projectViewModel.CurrentProject?.Segments == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                // Get duration from audio service
                TotalDuration = _audioService.GetDuration();
                RecalculatePixelsPerSecond();

                // Load segments ordered by StartTime
                var sortedSegments = _projectViewModel.CurrentProject.Segments
                    .OrderBy(s => s.StartTime)
                    .ToList();

                foreach (var segment in sortedSegments)
                {
                    Segments.Add(segment);
                }

                StatusMessage = $"Loaded {Segments.Count} segment(s)";
                Log.Information("Segments loaded: {Count}", Segments.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading segments: {ex.Message}";
                Log.Error(ex, "Error loading segments");
            }
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
        /// Check if two segments overlap.
        /// </summary>
        private bool HasOverlap(Segment s1, Segment s2)
        {
            // No overlap if s1 ends before s2 starts OR s2 ends before s1 starts
            return !(s1.EndTime <= s2.StartTime || s2.EndTime <= s1.StartTime);
        }

        /// <summary>
        /// Check if adding/modifying segment creates collision with others.
        /// </summary>
        private bool CheckCollision(Segment segment, Segment? excludeSegment = null)
        {
            foreach (var other in Segments)
            {
                if (excludeSegment != null && other.Id == excludeSegment.Id)
                    continue;

                if (HasOverlap(segment, other))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Snap time value to grid.
        /// </summary>
        private double SnapToGrid(double timeSeconds)
        {
            return Math.Round(timeSeconds / GridSize) * GridSize;
        }

        /// <summary>
        /// Add a new segment at playhead position.
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

                // Create new segment starting at playhead, 5 seconds duration
                var newSegment = new Segment
                {
                    ProjectId = _projectViewModel.CurrentProject.Id,
                    StartTime = SnapToGrid(PlayheadPosition),
                    EndTime = SnapToGrid(PlayheadPosition + 5.0),
                    Text = "New Segment",
                    TransitionType = "fade",
                    TransitionDuration = 0.5,
                    Order = Segments.Count
                };

                // Check for collision
                if (CheckCollision(newSegment))
                {
                    StatusMessage = "Segment overlaps with existing segment";
                    return;
                }

                Segments.Add(newSegment);
                SelectSegment(newSegment);
                StatusMessage = "Segment added";
                Log.Information("Segment added at {StartTime}s", newSegment.StartTime);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding segment: {ex.Message}";
                Log.Error(ex, "Error adding segment");
            }
        }

        /// <summary>
        /// Clear all segments from the timeline (ST-9 plan: ClearAllSegmentsCommand).
        /// </summary>
        [RelayCommand]
        public void ClearAllSegments()
        {
            try
            {
                if (Segments.Count == 0)
                {
                    StatusMessage = "Timeline already empty";
                    return;
                }

                int count = Segments.Count;
                Segments.Clear();
                SelectedSegment = null;
                StatusMessage = $"Cleared {count} segment(s)";
                Log.Information("Cleared all segments: {Count}", count);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error clearing segments: {ex.Message}";
                Log.Error(ex, "Error clearing segments");
            }
        }

        /// <summary>
        /// Delete selected segment.
        /// </summary>
        [RelayCommand]
        public void DeleteSelectedSegment()
        {
            try
            {
                if (SelectedSegment == null)
                {
                    StatusMessage = "No segment selected";
                    return;
                }

                Segments.Remove(SelectedSegment);
                SelectedSegment = null;
                StatusMessage = "Segment deleted";
                Log.Information("Segment deleted");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting segment: {ex.Message}";
                Log.Error(ex, "Error deleting segment");
            }
        }

        /// <summary>
        /// Duplicate selected segment with offset.
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

                var original = SelectedSegment;
                var offset = original.EndTime - original.StartTime + 0.5; // Original duration + 0.5s gap

                var duplicate = new Segment
                {
                    ProjectId = original.ProjectId,
                    StartTime = SnapToGrid(original.EndTime + 0.5),
                    EndTime = SnapToGrid(original.EndTime + 0.5 + offset),
                    Text = original.Text + " (Copy)",
                    BackgroundAssetId = original.BackgroundAssetId,
                    TransitionType = original.TransitionType,
                    TransitionDuration = original.TransitionDuration,
                    Order = Segments.Count
                };

                if (CheckCollision(duplicate))
                {
                    StatusMessage = "Duplicate would overlap with existing segment";
                    return;
                }

                Segments.Add(duplicate);
                SelectSegment(duplicate);
                StatusMessage = "Segment duplicated";
                Log.Information("Segment duplicated");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error duplicating segment: {ex.Message}";
                Log.Error(ex, "Error duplicating segment");
            }
        }

        /// <summary>
        /// Update segment timing with collision check. When overlap detected, snaps to nearest
        /// valid boundary (edge of blocking segment) to prevent jitter when dragging past.
        /// </summary>
        public bool UpdateSegmentTiming(Segment segment, double newStartTime, double newEndTime)
        {
            try
            {
                double duration = newEndTime - newStartTime;
                if (duration <= 0)
                {
                    StatusMessage = "Invalid segment timing";
                    return false;
                }

                const double timeTolerance = 0.001;
                bool resizeRightOnly = Math.Abs(newStartTime - segment.StartTime) < timeTolerance && newEndTime > segment.EndTime;
                bool resizeLeftOnly = Math.Abs(newEndTime - segment.EndTime) < timeTolerance; // only start changes (extend or shrink left)

                if (resizeRightOnly)
                {
                    // Only extend right edge: never change start; only clamp end.
                    newStartTime = segment.StartTime;
                    if (newEndTime > TotalDuration)
                        newEndTime = TotalDuration;
                    newEndTime = SnapToGrid(newEndTime);
                }
                else if (resizeLeftOnly)
                {
                    // Only extend left edge: never change end; only clamp start.
                    newEndTime = segment.EndTime;
                    if (newStartTime < 0)
                        newStartTime = 0;
                    newStartTime = SnapToGrid(newStartTime);
                }
                else
                {
                    // Move or other resize: allow clamping that may shift start.
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

                if (CheckCollision(testSegment, segment))
                {
                    if (resizeRightOnly)
                    {
                        // Cap end at the first segment to the right; never move start (no overlap).
                        double capAt = newEndTime;
                        foreach (var other in Segments)
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
                        // Cap start at the last segment to the left (previous); never move end.
                        double floorAt = newStartTime;
                        foreach (var other in Segments)
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
                        // Move or resize-left: use full boundary snap (before/after other segment)
                        var snapped = TrySnapToBoundary(segment, newStartTime, newEndTime);
                        if (snapped.HasValue)
                        {
                            newStartTime = snapped.Value.start;
                            newEndTime = snapped.Value.end;
                        }
                        else
                        {
                            StatusMessage = "Cannot move: overlaps with other segment";
                            return false;
                        }
                    }
                }

                // Apply changes
                segment.StartTime = newStartTime;
                segment.EndTime = newEndTime;

                var index = Segments.IndexOf(segment);
                if (index >= 0)
                {
                    Segments[index] = segment;
                }

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
        /// When overlap detected, find nearest valid position by snapping to blocking segment edge.
        /// Prefers snap closest to CURRENT position (not requested) to reduce oscillation.
        /// </summary>
        private (double start, double end)? TrySnapToBoundary(Segment segment, double requestedStart, double requestedEnd)
        {
            double duration = requestedEnd - requestedStart;
            double currentStart = segment.StartTime;
            (double start, double end)? best = null;
            double bestDistance = double.MaxValue;

            foreach (var other in Segments)
            {
                if (other.Id == segment.Id) continue;

                // Option A: Snap after other (place segment to the right)
                double startA = SnapToGrid(other.EndTime);
                double endA = startA + duration;
                if (startA >= 0 && endA <= TotalDuration)
                {
                    var testA = new Segment { Id = segment.Id, StartTime = startA, EndTime = endA };
                    if (!CheckCollision(testA, segment))
                    {
                        double dist = Math.Abs(startA - currentStart);
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            best = (startA, endA);
                        }
                    }
                }

                // Option B: Snap before other (place segment to the left)
                double endB = SnapToGrid(other.StartTime);
                double startB = endB - duration;
                if (startB >= 0 && endB <= TotalDuration)
                {
                    var testB = new Segment { Id = segment.Id, StartTime = startB, EndTime = endB };
                    if (!CheckCollision(testB, segment))
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
        /// Select a segment for editing.
        /// </summary>
        public void SelectSegment(Segment segment)
        {
            SelectedSegment = segment;
            StatusMessage = $"Selected: {segment.Text}";
        }

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
                StatusMessage = $"Playhead: {positionSeconds:F1}s";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Seek failed: {ex.Message}";
                Log.Warning(ex, "Seek failed");
            }
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
                        // Get current position from audio service
                        var currentPosition = _audioService.GetCurrentPosition();
                        PlayheadPosition = currentPosition;

                        // Update at ~30fps (33ms)
                        await Task.Delay(33, _playheadSyncCts.Token);
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
            Log.Information("Playhead sync stopped");
        }

        /// <summary>
        /// Handle timeline width change (from UI resize).
        /// </summary>
        partial void OnTimelineWidthChanged(double value)
        {
            RecalculatePixelsPerSecond();
        }

        /// <summary>
        /// Handle total duration change.
        /// </summary>
        partial void OnTotalDurationChanged(double value)
        {
            RecalculatePixelsPerSecond();
        }

        /// <summary>
        /// Cleanup on dispose.
        /// </summary>
        public void Dispose()
        {
            StopPlayheadSync();
        }
    }
}
