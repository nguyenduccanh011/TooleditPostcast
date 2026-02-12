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
                    LoadTracksFromProject();
                    ApplyScriptCommand.NotifyCanExecuteChanged();
                }
            };

            // Start playhead sync loop
            StartPlayheadSync();
        }

        /// <summary>
        /// Load tracks from current project, including all segments for each track.
        /// </summary>
        private void LoadTracksFromProject()
        {
            try
            {
                Tracks.Clear();
                SelectedTrack = null;
                SelectedSegment = null;

                if (_projectViewModel.CurrentProject?.Tracks == null || _projectViewModel.CurrentProject.Tracks.Count == 0)
                {
                    StatusMessage = "No project loaded or no tracks";
                    AudioPeaks = Array.Empty<float>();
                    return;
                }

                // Get duration from audio service
                TotalDuration = _audioService.GetDuration();
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
                SelectedTrack = sortedTracks.FirstOrDefault(t => t.TrackType == "visual") ?? sortedTracks.FirstOrDefault();

                StatusMessage = $"Loaded {Tracks.Count} track(s)";
                Log.Information("Tracks loaded: {Count}", Tracks.Count);

                // Load waveform peaks only when audio is already loaded
                if (_audioService.GetDuration() > 0)
                    _ = LoadAudioPeaksAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading tracks: {ex.Message}";
                Log.Error(ex, "Error loading tracks");
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
                var targetTrack = SelectedTrack?.TrackType == "visual"
                    ? SelectedTrack
                    : Tracks.FirstOrDefault(t => t.TrackType == "visual");

                if (targetTrack == null)
                {
                    StatusMessage = "No visual track found";
                    return;
                }

                // Create new segment starting at playhead, 5 seconds duration
                double endTime = PlayheadPosition + 5.0;
                if (TotalDuration > 0 && endTime > TotalDuration)
                    endTime = TotalDuration;

                var newSegment = new Segment
                {
                    ProjectId = _projectViewModel.CurrentProject.Id,
                    TrackId = targetTrack.Id,
                    StartTime = SnapToGrid(PlayheadPosition),
                    EndTime = SnapToGrid(endTime),
                    Text = "New Segment",
                    Kind = "visual",
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
                SelectSegment(newSegment);
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
        /// Apply pasted script: parse [start → end] text, replace text track segments, persist (ST-3, multi-track).
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

                // Find text track (first track with TrackType = "text")
                var textTrack = _projectViewModel.CurrentProject.Tracks?.FirstOrDefault(t => t.TrackType == "text");
                if (textTrack == null)
                {
                    StatusMessage = "No text track found in project";
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
                        TrackId = textTrack.Id,
                        StartTime = Math.Round(p.Start, 2),
                        EndTime = Math.Round(p.End, 2),
                        Text = p.Text,
                        Kind = "text",
                        TransitionType = "fade",
                        TransitionDuration = 0.5,
                        Order = i
                    });
                }

                await _projectViewModel.ReplaceSegmentsAndSaveAsync(newSegments);

                // Reload tracks from project
                LoadTracksFromProject();
                StatusMessage = $"Script applied: {newSegments.Count} segment(s) in text track";
                Log.Information("Script applied: {Count} segments in text track {TrackId}", newSegments.Count, textTrack.Id);
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

                if (SelectedTrack == null)
                {
                    StatusMessage = "No track context";
                    return;
                }

                SelectedTrack.Segments.Remove(SelectedSegment);
                SelectedSegment = null;
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
                var offset = original.EndTime - original.StartTime + 0.5; // Original duration + 0.5s gap

                var duplicate = new Segment
                {
                    ProjectId = original.ProjectId,
                    TrackId = original.TrackId,
                    StartTime = SnapToGrid(original.EndTime + 0.5),
                    EndTime = SnapToGrid(original.EndTime + 0.5 + offset),
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
                SelectSegment(duplicate);
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
        /// </summary>
        public void SelectSegment(Segment segment)
        {
            SelectedSegment = segment;
            
            // Also select the track containing this segment
            if (!string.IsNullOrWhiteSpace(segment.TrackId))
            {
                SelectedTrack = Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
            }
            
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
