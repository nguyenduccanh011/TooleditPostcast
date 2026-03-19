// CanvasViewModel.Preview.cs — Preview pipeline: attach wiring, change reactions, composition.
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class CanvasViewModel
    {
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
                _assetFrameCache.Clear();
                InvalidateAssetLookup();
                ClearActiveVisualCache();
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

            _timelineViewModel.InvalidateActiveSegmentsCachePublic();
            UpdateActivePreview(_timelineViewModel.PlayheadPosition);
        }

        private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_timelineViewModel == null)
                return;

            if (e.PropertyName == nameof(Segment.BackgroundAssetId))
            {
                ClearActiveVisualCache();
                UpdateActivePreview(_timelineViewModel.PlayheadPosition);
                return;
            }

            if (e.PropertyName == nameof(Segment.Text))
            {
                UpdateActivePreview(_timelineViewModel.PlayheadPosition);
                return;
            }

            if (e.PropertyName == nameof(Segment.StartTime)
                || e.PropertyName == nameof(Segment.EndTime)
                || e.PropertyName == nameof(Segment.Kind)
                || e.PropertyName == nameof(Segment.TrackId))
            {
                _timelineViewModel.InvalidateActiveSegmentsCachePublic();
                UpdateActivePreview(_timelineViewModel.PlayheadPosition);
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
            }
        }

        private void DetachTrackSubscriptions()
        {
            if (_trackedSegments != null && _segmentPropertyChangedHandler != null)
            {
                foreach (var segment in _trackedSegments)
                    segment.PropertyChanged -= _segmentPropertyChangedHandler;
            }

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

            // --- Collect ALL visual and text segments (multi-track composite) ---
            // GetActiveSegmentsAtTime already filters by Track.IsVisible and sorts by Track.Order (0=front)
            var visualPairs = new List<(Track track, Segment segment)>();
            var textPairs = new List<(Track track, Segment segment)>();

            foreach (var pair in active)
            {
                if (string.Equals(pair.track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase))
                    visualPairs.Add(pair);
                else if (string.Equals(pair.track.TrackType, TrackTypes.Text, StringComparison.OrdinalIgnoreCase))
                    textPairs.Add(pair);
                // Audio type segments are not visual — skip for preview rendering
            }

            // --- Multi-text composite: collect text from ALL active text tracks ---
            ActiveTextOverlays.Clear();
            foreach (var tp in textPairs)
            {
                if (!string.IsNullOrWhiteSpace(tp.segment.Text))
                    ActiveTextOverlays.Add(tp.segment.Text);
            }

            // Legacy single-text properties (kept for backward compatibility)
            var primaryTextPair = textPairs.Count > 0 ? textPairs[0] : default;
            ActiveTextSegment = primaryTextPair.segment;
            ActiveTextContent = string.Join("\n", ActiveTextOverlays);
            IsTextOverlayVisible = ActiveTextOverlays.Count > 0;

            // --- Multi-visual composite: iterate visual tracks by z-order ---
            // Strategy: frontmost video → MediaElement; other images → BackgroundLayers
            var primaryVisualPair = visualPairs.Count > 0 ? visualPairs[0] : default;
            ActiveVisualSegment = primaryVisualPair.segment;
            SetActiveVisualLayout(primaryVisualPair.track?.ImageLayoutPreset);

            bool videoHandled = false;
            BackgroundLayers.Clear();

            // Try to find a video asset in the frontmost visual segment
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

            // Load image layers for all visual segments (skip the one handled as video)
            // Add from back to front: highest Order (background) first in collection
            for (int i = visualPairs.Count - 1; i >= 0; i--)
            {
                var (track, segment) = visualPairs[i];

                // If this is the primary video segment already handled by MediaElement, skip
                if (videoHandled && i == 0)
                    continue;

                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                    continue;

                // If an interactive ImageElement exists for this segment, skip background rendering.
                // The element renders the image on the interactive canvas layer instead.
                if (Elements.Any(e => string.Equals(e.SegmentId, segment.Id, StringComparison.Ordinal)))
                    continue;

                var layerAsset = FindAssetById(segment.BackgroundAssetId);
                if (layerAsset == null)
                    continue;

                var layerFrame = LoadFrameForAsset(layerAsset, segment, playheadSeconds);
                var layer = CreatePreviewLayer(layerFrame, track);
                if (layer != null)
                    BackgroundLayers.Add(layer);
            }

            // Legacy single-image property: set to frontmost non-video visual (or null)
            // Skip if the primary segment has a linked canvas element (it renders via interactive layer).
            if (!videoHandled && visualPairs.Count > 0)
            {
                var primarySeg = primaryVisualPair.segment;
                bool primaryHasElement = primarySeg != null &&
                    Elements.Any(e => string.Equals(e.SegmentId, primarySeg.Id, StringComparison.Ordinal));

                if (primaryHasElement)
                {
                    ActiveVisualImage = null;
                }
                else
                {
                    var frontAsset = FindAssetById(primarySeg?.BackgroundAssetId);
                    ActiveVisualImage = frontAsset != null
                        ? LoadFrameForAsset(frontAsset, primarySeg!, playheadSeconds)
                        : null;
                }
            }
            else if (!videoHandled)
            {
                ActiveVisualImage = null;
            }

            IsVisualPlaceholderVisible = ActiveVisualSegment == null && BackgroundLayers.Count == 0 && !IsVideoMode;
            UpdateElementVisibility(active);
        }

        private PreviewVisualLayer? CreatePreviewLayer(BitmapSource? image, Track? track)
        {
            if (image == null)
                return null;

            var preset = track?.ImageLayoutPreset ?? ImageLayoutPresets.FullFrame;
            var (x, y, width, height) = global::PodcastVideoEditor.Core.RenderHelper.ComputeImageRect(preset, CanvasWidth, CanvasHeight);
            return new PreviewVisualLayer(image, x, y, width, height);
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
            ActiveVisualImage = null;
            VideoSource = null;
            IsVideoMode = false;
            ActiveTextContent = string.Empty;
            ActiveVisualSegment = null;
            ActiveTextSegment = null;
            IsTextOverlayVisible = false;
            IsVisualPlaceholderVisible = true;
            SetActiveVisualLayout(ImageLayoutPresets.FullFrame);
            BackgroundLayers.Clear();
            ActiveTextOverlays.Clear();
        }
    }

    public sealed class PreviewVisualLayer
    {
        public PreviewVisualLayer(BitmapSource image, double x, double y, double width, double height)
        {
            Image = image;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public BitmapSource Image { get; }

        public double X { get; }

        public double Y { get; }

        public double Width { get; }

        public double Height { get; }
    }
}
