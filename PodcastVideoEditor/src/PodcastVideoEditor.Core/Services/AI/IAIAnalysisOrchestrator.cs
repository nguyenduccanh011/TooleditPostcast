using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Result returned by the AI analysis orchestrator.
/// Caller is responsible for persisting these segments via ProjectService.
/// </summary>
public record OrchestratorResult(
    Segment[] TextSegments,
    Segment[] VisualSegments,
    string[] RegisteredAssetIds
);

/// <summary>
/// Full 6-step pipeline: normalize → analyze → fetch images → AI select → download → build segments.
/// </summary>
public interface IAIAnalysisOrchestrator
{
    Task<OrchestratorResult> RunAsync(
        Project project,
        string script,
        double? audioDuration = null,
        IProgress<AIAnalysisProgressReport>? progress = null,
        CancellationToken ct = default);
}
