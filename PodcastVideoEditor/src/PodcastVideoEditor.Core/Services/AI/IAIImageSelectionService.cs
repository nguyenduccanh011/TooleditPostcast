namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Coordinates fetching image candidates from all providers and running AI selection.
/// </summary>
public interface IAIImageSelectionService
{
    /// <summary>
    /// Fetch up to ~60 candidates for a single segment by searching all enabled providers
    /// using the segment's first 3 keywords combined.
    /// </summary>
    Task<(ImageCandidate[] Candidates, Dictionary<string, ImageCandidate> CandidateMap)>
        FetchCandidatesForSegmentAsync(AISegment segment, CancellationToken ct = default);

    /// <summary>
    /// For all segments: fetch candidates in parallel, then call AI selection in batches of 10
    /// with max 3 concurrent batches.  Retries up to 5 times for segments without a selection.
    /// Returns selection results AND a pre-built candidateId→url map so callers do not
    /// need to re-fetch candidates to obtain download URLs.
    /// </summary>
    Task<(AIImageSelectionResultItem[] Selections, IReadOnlyDictionary<string, string> CandidateUrls)> RunSelectBackgroundsAsync(
        AISegment[] segments,
        IProgress<AIAnalysisProgressReport>? progress = null,
        CancellationToken ct = default);
}
