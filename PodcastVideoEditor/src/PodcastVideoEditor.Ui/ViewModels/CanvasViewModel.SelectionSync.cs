using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using System;
using System.Linq;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class CanvasViewModel
    {
        /// <summary>
        /// Set selection sync service for bidirectional canvas↔timeline selection.
        /// Safely detaches from any previously assigned service before wiring the new one.
        /// </summary>
        public void SetSelectionSyncService(SelectionSyncService selectionSyncService)
        {
            if (_selectionSyncService != null)
                _selectionSyncService.TimelineSelectionChanged -= OnTimelineSegmentSelected;

            _selectionSyncService = selectionSyncService ?? throw new ArgumentNullException(nameof(selectionSyncService));
            _selectionSyncService.TimelineSelectionChanged += OnTimelineSegmentSelected;
        }

        private void OnTimelineSegmentSelected(string? segmentId, bool playheadInRange)
        {
            if (string.IsNullOrEmpty(segmentId))
            {
                SelectElement(null);
                return;
            }

            // Skip auto-creation when an Add*Element method is already in progress.
            // The calling method will add the element itself after segment creation.
            if (_isCreatingElement)
                return;

            // Highlight linked canvas element regardless of playhead position.
            var linked = Elements.FirstOrDefault(e => string.Equals(e.SegmentId, segmentId, StringComparison.Ordinal));

            // If no linked element exists, auto-create one for image or text segments.
            bool wasCreated = false;
            if (linked == null)
            {
                linked = (CanvasElement?)TryCreateImageElementForSegment(segmentId)
                      ?? GetOrCreateTextElement(segmentId);
                wasCreated = linked != null;
            }

            SelectElement(linked);

            // When a new element was just created, run a full preview update so that
            // UpdateElementVisibility sets the correct IsVisible based on playhead.
            if (wasCreated)
                UpdateActivePreview(_timelineViewModel?.PlayheadPosition ?? 0);
        }
    }
}
