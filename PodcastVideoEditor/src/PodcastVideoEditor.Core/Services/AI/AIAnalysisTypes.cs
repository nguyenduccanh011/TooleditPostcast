namespace PodcastVideoEditor.Core.Services.AI;

// ── Script Analysis types ────────────────────────────────────────────────────

/// <summary>
/// Input to the AI script analysis call.
/// </summary>
public record AIAnalysisRequest(
    string Script,
    double? AudioDuration = null,
    string? Model = null,
    double Temperature = 0.7,
    int MaxTokens = 8192,
    string? SystemPrompt = null
);

/// <summary>
/// A single segment returned by the AI script analysis.
/// Timestamps are in seconds from start of audio.
/// Keywords is a 5-element array of English search terms.
/// </summary>
public record AISegment(
    double StartTime,
    double EndTime,
    string Text,
    string[] Keywords,
    string? Description = null
);

/// <summary>
/// Full response from the AI script analysis pipeline.
/// </summary>
public record AIAnalysisResponse(
    AISegment[] Segments,
    string RawResponse
);

/// <summary>
/// Progress report emitted by the orchestrator via IProgress&lt;T&gt;.
/// Step ranges from 1–6 matching the 6 pipeline stages.
/// </summary>
public record AIAnalysisProgressReport(
    int Step,
    int Percent,
    string Message,
    bool IsError = false,
    string? ErrorDetail = null
);
