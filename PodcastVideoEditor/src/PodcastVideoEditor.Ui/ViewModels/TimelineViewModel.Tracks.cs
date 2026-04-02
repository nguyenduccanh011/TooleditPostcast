#nullable enable
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    // ── Track operations: add, remove, reorder, lock, visibility ──

    public partial class TimelineViewModel
    {
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
                TrackTypes.Effect => "Effect",
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

        /// <summary>
        /// Raised when the user applies a ScaleMode to all segments in a track from the Track Properties panel.
        /// CanvasViewModel subscribes to update matching ImageElement/LogoElement instances.
        /// </summary>
        public event Action<string, ScaleMode>? TrackScaleModeApplied;

        /// <summary>
        /// Apply a ScaleMode to all image/logo elements belonging to a track's segments.
        /// </summary>
        public void ApplyScaleModeToTrack(Track track, ScaleMode scaleMode)
        {
            if (track == null) return;
            TrackScaleModeApplied?.Invoke(track.Id, scaleMode);
            RequestProjectSave();
            Log.Information("Track '{Name}' ScaleMode batch-applied: {Mode}", track.Name, scaleMode);
        }

        /// <summary>
        /// Remove a track (must be empty — no segments). Audio tracks with waveform-only are removable.
        /// Awaits DB deletion to prevent race conditions with autosave/navigation reload.
        /// Rolls back in-memory removal on DB failure so UI stays consistent with DB.
        /// </summary>
        public async Task<bool> RemoveTrackAsync(Track track)
        {
            if (track == null)
                return false;

            if (track.Segments != null && track.Segments.Count > 0)
            {
                StatusMessage = "Cannot remove track with segments. Delete segments first.";
                return false;
            }

            var trackId = track.Id;
            var trackOrder = track.Order;

            // Remove from the ViewModel collection
            Tracks.Remove(track);

            // Also remove from the project model by ID (handles any collection type
            // that EF Core may use, e.g. HashSet<Track> for ICollection<T>).
            var projectTracks = _projectViewModel.CurrentProject?.Tracks;
            Track? removedModelTrack = null;
            if (projectTracks != null)
            {
                removedModelTrack = projectTracks.FirstOrDefault(t => t.Id == trackId);
                if (removedModelTrack != null)
                    projectTracks.Remove(removedModelTrack);
            }

            // Re-index remaining track orders (both collections share the same Track instances)
            int order = 0;
            foreach (var t in Tracks.OrderBy(t => t.Order))
                t.Order = order++;

            // Await DB deletion so it completes before any subsequent autosave
            // or project reload can query the database.
            try
            {
                await _projectViewModel.ProjectService.DeleteTrackAsync(trackId);
                Log.Information("Track {TrackId} deleted from database", trackId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete track {TrackId} from database — rolling back", trackId);

                // Rollback: re-add to both collections so UI stays consistent with DB
                track.Order = trackOrder;
                Tracks.Add(track);
                if (removedModelTrack != null)
                    projectTracks?.Add(removedModelTrack);

                // Re-index after rollback
                order = 0;
                foreach (var t in Tracks.OrderBy(t => t.Order))
                    t.Order = order++;

                StatusMessage = $"Failed to remove track: {ex.Message}";
                return false;
            }

            StatusMessage = $"Removed track: {track.Name}";
            Log.Information("Track removed: {Name} ({Type})", track.Name, track.TrackType);
            return true;
        }

        /// <summary>
        /// Move a track one position towards front (decrease Order → higher visual priority).
        /// In the timeline, the track moves up.
        /// </summary>
        public void MoveTrackUp(Track? track)
        {
            if (track == null) return;

            // Find the track with the next-lower Order (the one just above in the timeline)
            var aboveTrack = Tracks
                .Where(t => t.Order < track.Order)
                .OrderByDescending(t => t.Order)
                .FirstOrDefault();

            if (aboveTrack == null)
            {
                StatusMessage = $"Track '{track.Name}' is already at the top";
                return;
            }

            // Swap Order values
            (aboveTrack.Order, track.Order) = (track.Order, aboveTrack.Order);
            RebuildTrackCollectionOrder();
            _ = _projectViewModel.SaveProjectAsync();
            StatusMessage = $"Moved track '{track.Name}' up";
            Log.Information("Track moved up: {Name} → Order {Order}", track.Name, track.Order);
        }

        /// <summary>
        /// Move a track one position towards back (increase Order → lower visual priority).
        /// In the timeline, the track moves down.
        /// </summary>
        public void MoveTrackDown(Track? track)
        {
            if (track == null) return;

            // Find the track with the next-higher Order (the one just below in the timeline)
            var belowTrack = Tracks
                .Where(t => t.Order > track.Order)
                .OrderBy(t => t.Order)
                .FirstOrDefault();

            if (belowTrack == null)
            {
                StatusMessage = $"Track '{track.Name}' is already at the bottom";
                return;
            }

            // Swap Order values
            (belowTrack.Order, track.Order) = (track.Order, belowTrack.Order);
            RebuildTrackCollectionOrder();
            _ = _projectViewModel.SaveProjectAsync();
            StatusMessage = $"Moved track '{track.Name}' down";
            Log.Information("Track moved down: {Name} → Order {Order}", track.Name, track.Order);
        }

        /// <summary>
        /// Reorder a track to a new visual position in the timeline.
        /// Lower index means closer to the top/front.
        /// </summary>
        public void ReorderTrack(Track? track, int newIndex)
        {
            if (track == null)
                return;

            var orderedTracks = Tracks.OrderBy(t => t.Order).ToList();
            var currentIndex = orderedTracks.IndexOf(track);
            if (currentIndex < 0)
                return;

            newIndex = Math.Max(0, Math.Min(newIndex, orderedTracks.Count - 1));
            if (currentIndex == newIndex)
                return;

            orderedTracks.RemoveAt(currentIndex);
            orderedTracks.Insert(newIndex, track);

            for (int i = 0; i < orderedTracks.Count; i++)
                orderedTracks[i].Order = i;

            RebuildTrackCollectionOrder();
            _ = _projectViewModel.SaveProjectAsync();
            StatusMessage = $"Reordered track '{track.Name}'";
            Log.Information("Track reordered: {Name} → Index {Index}", track.Name, newIndex);
        }

        /// <summary>
        /// Resort the Tracks ObservableCollection to reflect current Order values.
        /// Preserves data binding — uses in-place sorting via move operations.
        /// </summary>
        private void RebuildTrackCollectionOrder()
        {
            var sorted = Tracks.OrderBy(t => t.Order).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int currentIndex = Tracks.IndexOf(sorted[i]);
                if (currentIndex != i)
                    Tracks.Move(currentIndex, i);
            }
        }
    }
}
