namespace PodcastVideoEditor.Core.Services.AI;

// ── Image search / selection types ──────────────────────────────────────────

/// <summary>
/// An image candidate returned by a search provider.
/// Id uses the format "provider:nativeId", e.g. "pexels:12345".
/// </summary>
public record ImageCandidate(
    string Id,
    string Semantic,
    string Url,
    string ThumbnailUrl,
    string Provider
);

/// <summary>
/// Container for one segment's context + its pool of image candidates,
/// passed to the AI image-selection call.
/// Context should be at most ~300 characters.
/// </summary>
public record SegmentWithCandidates(
    int SegmentIndex,
    string Context,
    ImageCandidate[] Candidates
);

/// <summary>
/// The AI's selection result for a single segment.
/// ChosenId and BackupIds use the same "provider:id" format.
/// </summary>
public record AIImageSelectionResultItem(
    int SegmentIndex,
    string ChosenId,
    string[] BackupIds,
    string Reason
);
