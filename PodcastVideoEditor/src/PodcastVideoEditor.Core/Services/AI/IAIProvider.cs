namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Abstraction over the YesScale (OpenAI-compatible) chat completion API.
/// </summary>
public interface IAIProvider
{
    /// <summary>
    /// Fetches the list of available model IDs for a specific API key/base URL combination.
    /// Used by Settings UI before the configuration is persisted.
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        string apiKey,
        string? baseUrl = null,
        CancellationToken ct = default);

    /// <summary>
    /// Normalize the raw ASR script: fix obvious mis-transcriptions while preserving timestamps.
    /// Uses temperature=0.3, timeout 60 s.
    /// Returns the normalized script text, or the original on timeout.
    /// </summary>
    Task<string> NormalizeScriptAsync(string script, CancellationToken ct = default);

    /// <summary>
    /// Analyze the script and produce one AISegment per timestamp block.
    /// Includes ASR correction. Each segment has 3 English keywords for image search.
    /// Uses temperature=0.3, maxTokens=8192, timeout 90 s.
    /// </summary>
    Task<AIAnalysisResponse> AnalyzeScriptAsync(AIAnalysisRequest request, CancellationToken ct = default);

    /// <summary>
    /// For each SegmentWithCandidates, have the AI pick the best image (+ 2-3 backups).
    /// Uses temperature=0.3, maxTokens=2048, timeout 120 s per call.
    /// </summary>
    Task<AIImageSelectionResultItem[]> SelectImagesAsync(
        SegmentWithCandidates[] segments, CancellationToken ct = default);

    /// <summary>
    /// Verify the API key is valid by calling GET /models.
    /// Returns true when the server responds 200 within 30 s.
    /// </summary>
    Task<bool> ValidateApiKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Run a lightweight health check on all configured models (primary + fallbacks).
    /// Dead models are cached so the fallback chain skips them instantly for subsequent calls.
    /// Call this once before starting a pipeline to avoid wasting time on dead models.
    /// </summary>
    Task RunModelHealthCheckAsync(CancellationToken ct = default);
}
