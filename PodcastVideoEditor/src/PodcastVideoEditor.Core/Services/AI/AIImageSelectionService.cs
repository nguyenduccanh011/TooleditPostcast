using System.Diagnostics;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Fetches image candidates from all providers and orchestrates batch AI image selection.
/// </summary>
public sealed class AIImageSelectionService : IAIImageSelectionService
{
    private const int BatchSize   = 15;
    private const int MaxConcurrent = 2;  // Reduced from 4 to prevent rate-limiting when using fewer models
    private const int MaxConcurrentFetch = 8;
    private const int MaxRetries  = 2;
    private const int PerProvider = 6; // images per provider per keyword query (6×3=18 candidates total)

    private readonly IReadOnlyList<IImageSearchProvider> _providers;
    private readonly IAIProvider _aiProvider;

    public AIImageSelectionService(IEnumerable<IImageSearchProvider> providers, IAIProvider aiProvider)
    {
        _providers  = providers.ToList();
        _aiProvider = aiProvider;
    }

    // ── FetchCandidatesForSegment ────────────────────────────────────────────

    public async Task<(ImageCandidate[] Candidates, Dictionary<string, ImageCandidate> CandidateMap)>
        FetchCandidatesForSegmentAsync(AISegment segment, CancellationToken ct = default)
    {
        try
        {
            var query = BuildQuery(segment.Keywords);
            var tasks = _providers.Select(p => p.SearchAsync(query, PerProvider, ct)).ToArray();
            var results = await Task.WhenAll(tasks);

            var candidates = results.SelectMany(r => r).ToArray();
            // Deduplicate by Id before building map (duplicate IDs across providers would throw)
            var unique = candidates
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
            var map = unique.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            return (unique, map);
        }
        catch (OperationCanceledException)
        {
            return ([], []);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FetchCandidatesForSegmentAsync failed");
            return ([], []);
        }
    }

    // ── RunSelectBackgrounds ─────────────────────────────────────────────────

    public async Task<(AIImageSelectionResultItem[] Selections, IReadOnlyDictionary<string, string> CandidateUrls)> RunSelectBackgroundsAsync(
        AISegment[] segments,
        IProgress<AIAnalysisProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        // Step 1: fetch candidates for all segments in parallel
        progress?.Report(new AIAnalysisProgressReport(3, 50, $"Tìm kiếm ảnh cho {segments.Length} segment..."));
        var fetchSw = Stopwatch.StartNew();
        Log.Information("[AI-IMG] fetch candidates: {Segments} segments × {Providers} providers",
            segments.Length, _providers.Count);

        using var fetchSem = new SemaphoreSlim(MaxConcurrentFetch);
        var fetchTasks = segments.Select(async (seg, idx) =>
        {
            await fetchSem.WaitAsync(ct);
            try
            {
                var result = await FetchCandidatesForSegmentAsync(seg, ct);
                return (idx, result.Candidates);
            }
            catch (OperationCanceledException)
            {
                return (idx, Array.Empty<ImageCandidate>());
            }
            finally
            {
                fetchSem.Release();
            }
        });

        var fetched = await Task.WhenAll(fetchTasks);
        var candidatesPerSegment = fetched.ToDictionary(r => r.idx, r => r.Item2);
        fetchSw.Stop();
        var totalCandidates = candidatesPerSegment.Values.Sum(c => c.Length);
        var emptySeg = candidatesPerSegment.Values.Count(c => c.Length == 0);
        Log.Information("[AI-IMG] fetch done in {Elapsed}s — {Total} candidates, {Empty} segments with no candidates",
            fetchSw.Elapsed.TotalSeconds.ToString("F1"), totalCandidates, emptySeg);

        // Step 2: slice into batches, call AI with concurrency limit
        progress?.Report(new AIAnalysisProgressReport(4, 60, "AI đang chọn ảnh..."));
        var selectSw = Stopwatch.StartNew();

        var allResults = new List<AIImageSelectionResultItem>();
        var sem = new SemaphoreSlim(MaxConcurrent);
        var batches = segments
            .Select((s, i) => new { Segment = s, Index = i })
            .Chunk(BatchSize)
            .ToArray();

        var batchTasks = batches.Select((batch, batchIdx) => Task.Run(async () =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var swcArr = batch
                    .Select(item =>
                    {
                        var cands = candidatesPerSegment.TryGetValue(item.Index, out var c) ? c : [];
                        var context = TruncateContext(item.Segment.Text, 300);
                        return new SegmentWithCandidates(item.Index, context, cands);
                    })
                    .Where(s => s.Candidates.Length > 0)
                    .ToArray();

                if (swcArr.Length == 0)
                {
                    Log.Warning("[AI-IMG] batch {Idx}: all {Count} segments had no candidates — skipping",
                        batchIdx + 1, batch.Length);
                    return Array.Empty<AIImageSelectionResultItem>();
                }
                return await _aiProvider.SelectImagesAsync(swcArr, ct);
            }
            finally
            {
                sem.Release();
            }
        }, ct)).ToArray();

        var batchResults = await Task.WhenAll(batchTasks);
        allResults.AddRange(batchResults.SelectMany(r => r));
        selectSw.Stop();
        Log.Information("[AI-IMG] initial selection in {Elapsed}s — {Selected}/{Total} segments selected",
            selectSw.Elapsed.TotalSeconds.ToString("F1"), allResults.Count, segments.Length);

        // Step 3: retry segments that were missed (up to MaxRetries), using batch parallelism
        var selectedIndexes = allResults.Select(r => r.SegmentIndex).ToHashSet();
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            var missing = Enumerable.Range(0, segments.Length)
                .Where(i => !selectedIndexes.Contains(i) &&
                            candidatesPerSegment.TryGetValue(i, out var c) && c.Length > 0)
                .ToArray();

            if (missing.Length == 0) break;

            Log.Warning("[AI-IMG] retry {Retry}/{Max}: {Count} segments still unselected — segment indices: [{Indices}]",
                retry + 1, MaxRetries, missing.Length, string.Join(",", missing));

            // Batch missing segments and retry in parallel (same pattern as initial call)
            var retryBatches = missing.Chunk(BatchSize).ToArray();
            var retryTasks = retryBatches.Select(batch => Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var retryCandidates = batch.Select(i =>
                    {
                        var cands = candidatesPerSegment[i];
                        return new SegmentWithCandidates(i, TruncateContext(segments[i].Text, 300), cands);
                    }).ToArray();
                    return await _aiProvider.SelectImagesAsync(retryCandidates, ct);
                }
                finally
                {
                    sem.Release();
                }
            }, ct)).ToArray();

            var retryBatchResults = await Task.WhenAll(retryTasks);
            foreach (var retryResults in retryBatchResults)
            {
                allResults.AddRange(retryResults);
                foreach (var r in retryResults) selectedIndexes.Add(r.SegmentIndex);
            }
        }

        var finalUnselected = Enumerable.Range(0, segments.Length).Except(selectedIndexes).ToArray();
        if (finalUnselected.Length > 0)
            Log.Warning("[AI-IMG] {Count} segments have NO image after all retries: [{Indices}]",
                finalUnselected.Length, string.Join(",", finalUnselected));

        progress?.Report(new AIAnalysisProgressReport(4, 90, $"Đã chọn ảnh cho {allResults.Count}/{segments.Length} segment"));

        // Build id→url map for selected candidates only (chosen + backups) to minimize memory usage.
        // No need to keep all 1000+ candidate URLs when only ~30-90 were actually selected.
        var selectedIds = allResults
            .SelectMany(r => r.BackupIds.Prepend(r.ChosenId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateUrls = candidatesPerSegment.Values
            .SelectMany(arr => arr)
            .Where(c => selectedIds.Contains(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Url, StringComparer.OrdinalIgnoreCase);

        return ([.. allResults], candidateUrls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildQuery(string[] keywords)
        => string.Join(" ", keywords.Take(3));

    private static string TruncateContext(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "…";
}
