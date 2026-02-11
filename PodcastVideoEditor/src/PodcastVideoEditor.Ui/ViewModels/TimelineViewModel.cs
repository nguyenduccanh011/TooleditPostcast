#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
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
        private double gridSize = 0.01; // 10ms grid snapping (2 decimal places, matches script [start → end])

        [ObservableProperty]
        private string statusMessage = "Timeline ready";

        /// <summary>
        /// Raw script text for paste (ST-3). Format: [start → end] text per line.
        /// </summary>
        [ObservableProperty]
        private string scriptPasteText = string.Empty;

        /// <summary>
        /// Peak samples for waveform display (audio track in timeline). ST-1 / Issue #13.
        /// </summary>
        [ObservableProperty]
        private float[] audioPeaks = Array.Empty<float>();

        private const int WaveformBinCount = 2400;
        private int _audioPeaksLoadVersion;

        /// <summary>Total width of timeline content (label column + timeline) for alignment. ST-1.</summary>
        public double TimelineContentWidth => TimelineWidth + 56;
        /// <summary>Width of audio waveform content (duration only, no extra buffer).</summary>
        public double AudioContentWidth => TimeToPixels(TotalDuration);

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
                    ApplyScriptCommand.NotifyCanExecuteChanged();
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
                    AudioPeaks = Array.Empty<float>();
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

                // Load waveform peaks only when audio is already loaded (avoid race: open project
                // triggers this before LoadProjectAudioAsync; OnAudioLoaded will refresh after load).
                if (_audioService.GetDuration() > 0)
                    _ = LoadAudioPeaksAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading segments: {ex.Message}";
                Log.Error(ex, "Error loading segments");
            }
        }

        /// <summary>
        /// Load peak samples for timeline audio track waveform. ST-1 / Issue #13.
        /// </summary>
        private async Task LoadAudioPeaksAsync()
        {
            try
            {
                int loadVersion = Interlocked.Increment(ref _audioPeaksLoadVersion);
                var expectedPath = _audioService.CurrentAudioPath;
                var peaks = await Task.Run(() => _audioService.GetPeakSamples(WaveformBinCount)).ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (loadVersion != _audioPeaksLoadVersion)
                        return;
                    if (!string.Equals(expectedPath, _audioService.CurrentAudioPath, StringComparison.OrdinalIgnoreCase))
                        return;
                    AudioPeaks = peaks ?? Array.Empty<float>();
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load audio peaks for timeline");
                await Application.Current.Dispatcher.InvokeAsync(() => { AudioPeaks = Array.Empty<float>(); });
            }
        }

        /// <summary>
        /// Call after audio is loaded (e.g. from MainWindow) so waveform appears. ST-1.
        /// </summary>
        public Task RefreshAudioPeaksAsync()
        {
            if (_audioService.GetDuration() <= 0)
                return Task.CompletedTask;
            return LoadAudioPeaksAsync();
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
        /// Snap time value to grid (2 decimal places to match script format).
        /// </summary>
        private double SnapToGrid(double timeSeconds)
        {
            return Math.Round(Math.Round(timeSeconds / GridSize) * GridSize, 2);
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
        /// Apply pasted script: parse [start → end] text, replace all segments, persist (ST-3).
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanApplyScript))]
        public async Task ApplyScriptAsync()
        {
            try
            {
                if (_projectViewModel.CurrentProject == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                var parsed = ScriptParser.Parse(ScriptPasteText);
                if (parsed.Count == 0)
                {
                    StatusMessage = "No valid segments (format: [start → end] text)";
                    return;
                }

                var projectId = _projectViewModel.CurrentProject.Id;
                var newSegments = new List<Segment>();
                for (int i = 0; i < parsed.Count; i++)
                {
                    var p = parsed[i];
                    newSegments.Add(new Segment
                    {
                        ProjectId = projectId,
                        StartTime = Math.Round(p.Start, 2),
                        EndTime = Math.Round(p.End, 2),
                        Text = p.Text,
                        TransitionType = "fade",
                        TransitionDuration = 0.5,
                        Order = i
                    });
                }

                await _projectViewModel.ReplaceSegmentsAndSaveAsync(newSegments);

                Segments.Clear();
                SelectedSegment = null;
                foreach (var s in _projectViewModel.CurrentProject!.Segments)
                    Segments.Add(s);
                StatusMessage = $"Script applied: {newSegments.Count} segment(s)";
                Log.Information("Script applied: {Count} segments", newSegments.Count);
            }
            catch (Exception ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                StatusMessage = $"Error applying script: {message}";
                Log.Error(ex, "Error applying script: {Message}", ex.Message);
            }
        }

        private bool CanApplyScript() => _projectViewModel.CurrentProject != null && !string.IsNullOrWhiteSpace(ScriptPasteText);

        partial void OnScriptPasteTextChanged(string value) => ApplyScriptCommand.NotifyCanExecuteChanged();

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
                        var currentPosition = _audioService.GetCurrentPosition();
                        
                        // Clamp to total duration to prevent playhead overshoot
                        if (TotalDuration > 0 && currentPosition > TotalDuration)
                            currentPosition = TotalDuration;
                        
                        // Update on UI thread with Background priority to not block user interactions
                        // This makes the UI feel more responsive
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PlayheadPosition = currentPosition;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                        
                        await Task.Delay(33, _playheadSyncCts.Token); // 30fps
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
        public void Dispose()
        {
            StopPlayheadSync();
        }
    }
}
