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
        return await FetchCandidatesForSegmentAsync(segment, null, ct);
    }

    private async Task<(ImageCandidate[] Candidates, Dictionary<string, ImageCandidate> CandidateMap)>
        FetchCandidatesForSegmentAsync(AISegment segment, HashSet<string>? globalSeen, CancellationToken ct = default)
    {
        try
        {
            var queries = BuildDiverseQueries(segment.Keywords);
            var allCandidates = new List<ImageCandidate>();

            foreach (var query in queries)
            {
                var tasks = _providers.Select(p => p.SearchAsync(query, PerProvider, ct)).ToArray();
                var results = await Task.WhenAll(tasks);
                allCandidates.AddRange(results.SelectMany(r => r));

                // If we have enough NEW candidates (not seen by other segments), stop early.
                if (globalSeen != null)
                {
                    var newCount = allCandidates.Count(c => !globalSeen.Contains(c.Id));
                    if (newCount >= PerProvider * 2) break; // 12+ fresh candidates → good enough
                }
                else if (allCandidates.Count >= PerProvider * _providers.Count)
                {
                    break; // Single-segment mode — first query is enough
                }
            }

            // Deduplicate by Id before building map (duplicate IDs across providers would throw)
            var unique = allCandidates
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            // When global tracking is active, deprioritize already-seen candidates:
            // put unseen first so the AI is more likely to pick fresh images.
            if (globalSeen != null && globalSeen.Count > 0)
            {
                unique = unique
                    .OrderBy(c => globalSeen.Contains(c.Id) ? 1 : 0)
                    .ToArray();
                foreach (var c in unique) globalSeen.Add(c.Id);
            }

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

        // Track globally-seen candidate IDs so each segment tries to fetch fresh images.
        // Sequential fetch (not parallel) is intentional: later segments can detect overlap
        // with earlier segments and automatically query additional keyword variations.
        var globalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatesPerSegment = new Dictionary<int, ImageCandidate[]>();
        using var fetchSem = new SemaphoreSlim(MaxConcurrentFetch);
        var fetchTasks = segments.Select(async (seg, idx) =>
        {
            await fetchSem.WaitAsync(ct);
            try
            {
                var result = await FetchCandidatesForSegmentAsync(seg, globalSeen, ct);
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
        foreach (var r in fetched) candidatesPerSegment[r.idx] = r.Item2;
        fetchSw.Stop();
        var totalCandidates = candidatesPerSegment.Values.Sum(c => c.Length);
        var uniqueCandidates = candidatesPerSegment.Values
            .SelectMany(c => c).Select(c => c.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var emptySeg = candidatesPerSegment.Values.Count(c => c.Length == 0);
        Log.Information("[AI-IMG] fetch done in {Elapsed}s — {Total} candidates ({Unique} unique), {Empty} segments with no candidates",
            fetchSw.Elapsed.TotalSeconds.ToString("F1"), totalCandidates, uniqueCandidates, emptySeg);

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

        // Retry batches can return multiple entries for the same segment index.
        // Keep only the latest result per segment to avoid downstream key collisions.
        var duplicateSegmentCount = allResults
            .GroupBy(r => r.SegmentIndex)
            .Count(g => g.Count() > 1);
        if (duplicateSegmentCount > 0)
        {
            Log.Warning("[AI-IMG] {Count} duplicate segment selection(s) detected; keeping latest result per segment", duplicateSegmentCount);
            allResults = allResults
                .GroupBy(r => r.SegmentIndex)
                .Select(g => g.Last())
                .OrderBy(r => r.SegmentIndex)
                .ToList();
        }

        // ── Dedup pass: prevent the same image being used for multiple segments ─
        // The AI sees each batch in isolation and often picks the same "best" image
        // for segments with similar topics/keywords. Process in segment order:
        //   1. If chosenId is unused → keep as-is.
        //   2. If duplicate → try each backupId in turn (first unused wins).
        //   3. If all backups also used → pick first unused candidate from pool.
        //   4. If pool exhausted → keep the duplicate (no alternatives left).
        var usedImageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedupedResults = new List<AIImageSelectionResultItem>(allResults.Count);
        foreach (var r in allResults.OrderBy(x => x.SegmentIndex))
        {
            if (usedImageIds.Add(r.ChosenId))
            {
                // First occurrence — keep as-is.
                dedupedResults.Add(r);
                continue;
            }

            // Chosen image is a duplicate — find a replacement.
            var pool = candidatesPerSegment.TryGetValue(r.SegmentIndex, out var c) ? c : [];

            // Try backup IDs first (already AI-vetted alternatives).
            var replacement = r.BackupIds.FirstOrDefault(id => usedImageIds.Add(id));

            // Fall back to any unused candidate in the pool.
            replacement ??= pool.Select(img => img.Id)
                               .FirstOrDefault(id => !usedImageIds.Contains(id) && usedImageIds.Add(id));

            if (replacement != null)
            {
                Log.Information("[AI-IMG] dedup: segment {Idx} — replaced duplicate '{Old}' with '{New}'",
                    r.SegmentIndex, r.ChosenId, replacement);
                dedupedResults.Add(r with { ChosenId = replacement });
            }
            else
            {
                Log.Warning("[AI-IMG] dedup: segment {Idx} — no unused candidates, keeping duplicate '{Id}'",
                    r.SegmentIndex, r.ChosenId);
                dedupedResults.Add(r);
            }
        }

        var dedupCount = allResults.Count(r =>
            dedupedResults.Any(d => d.SegmentIndex == r.SegmentIndex && d.ChosenId != r.ChosenId));
        if (dedupCount > 0)
            Log.Information("[AI-IMG] dedup: replaced {Count} duplicate image(s)", dedupCount);

        allResults = dedupedResults;

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

    /// <summary>
    /// Build multiple diverse search queries from 5 keywords.
    /// First query uses keywords[0..2], second uses keywords[2..4], etc.
    /// This ensures segments with the same first keyword still get
    /// different candidate pools via subsequent queries.
    /// </summary>
    private static string[] BuildDiverseQueries(string[] keywords)
    {
        if (keywords.Length <= 3)
            return [string.Join(" ", keywords)];

        // Primary: first 3 keywords (most specific)
        var q1 = string.Join(" ", keywords.Take(3));
        // Secondary: keywords[1] + keywords[3..] (different combination)
        var q2 = string.Join(" ", keywords.Skip(1).Take(3));
        // Tertiary: keywords[0] + keywords[3..4] (yet another mix)
        var remaining = keywords.Skip(3).Take(2).ToArray();
        var q3 = remaining.Length > 0
            ? string.Join(" ", remaining.Prepend(keywords[0]))
            : null;

        var queries = new List<string> { q1, q2 };
        if (q3 != null && q3 != q1 && q3 != q2) queries.Add(q3);
        return [.. queries];
    }

    private static string TruncateContext(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "…";
}
