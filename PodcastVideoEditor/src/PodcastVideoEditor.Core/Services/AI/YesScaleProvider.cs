using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using PodcastVideoEditor.Core.Utilities;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Represents the result of a single chat completion call, including finish metadata.
/// </summary>
internal record ChatResult(string Content, string FinishReason);

/// <summary>
/// Exception carrying the HTTP status code so retry logic can distinguish transient vs permanent failures.
/// </summary>
public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    public ApiException(int statusCode, string message, Exception inner) : base(message, inner) => StatusCode = statusCode;
    /// <summary>True for 429 (rate-limit) and 5xx server errors — safe to retry.</summary>
    public bool IsTransient => StatusCode == 429 || StatusCode >= 500;
    /// <summary>True when the API key does not have access to this model (permanent config error — never retry).</summary>
    public bool IsChannelError => StatusCode == 503 && Message.Contains("No available channel");
}

/// <summary>
/// Calls the YesScale OpenAI-compatible API for both script analysis and image selection.
/// </summary>
public sealed class YesScaleProvider : IAIProvider
{
    // Shared static HttpClient — do NOT dispose; lives for app lifetime.
    private static readonly HttpClient _sharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // ── Model health tracking ────────────────────────────────────────────────
    // After a model fails, remember it so subsequent requests skip it immediately
    // instead of wasting 10-30s on the fallback chain each time.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _deadModels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DeadModelCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromSeconds(7);

    // Round-robin counter for distributing requests across healthy models to prevent rate-limiting
    private int _roundRobinCounter;

    // ── System prompts ───────────────────────────────────────────────────────

    private const string ScriptAnalysisSystemPrompt = """
        # Role
        You are a Vietnamese podcast video editor. You group transcript lines into visual SCENES for background image changes.

        # Task
        Given a timestamped transcript in [start → end] format:
        1. Fix ASR errors — especially Vietnamese proper nouns, financial terms (cổ phiếu, trái phiếu, VN-Index…), and numbers
        2. Keep the original language (Vietnamese) — do NOT translate
        3. **MERGE adjacent lines into SCENES**: each scene should be 10–15 seconds long, covering 2–5 consecutive transcript lines that share the same topic/idea
        4. Each scene = 1 background image change in the video
        5. Generate exactly 5 English keywords per scene for stock image search — keywords MUST be unique across scenes (do NOT repeat the same keyword set for different scenes)

        # Scene merging rules
        - Target duration: 10–15 seconds per scene. Minimum 6s, maximum 20s
        - **CRITICAL: NEVER create a scene longer than 20 seconds. If a topic spans >20s, split it into multiple scenes.**
        - Group lines by topic continuity — when the speaker changes subject, start a new scene
        - startTime = first line's start, endTime = last line's end
        - Combine all text from merged lines into one "text" field (keep Vietnamese)
        - Do NOT create 1-line scenes unless a single line is already ≥10s
        - You MUST cover ALL transcript lines — every line must belong to a scene. Do NOT skip any lines.

        # Output
        Return ONLY a JSON array — no markdown fences, no explanation:
        [{"startTime":0.0,"endTime":12.38,"text":"combined corrected text of all merged lines","keywords":["kw1","kw2","kw3","kw4","kw5"]}]

        # Keyword rules
        - English only, concrete, visually searchable nouns/phrases
        - 5 keywords per scene, ordered by specificity (most specific first)
        - First 2 keywords: SPECIFIC to this scene's topic (e.g. "lottery winner", "VN-Index chart", "bankruptcy court")
        - Last 3 keywords: broader related terms for image search variety
        - CRITICAL: adjacent scenes MUST have DIFFERENT first 2 keywords — do NOT use the same generic terms like "stock market" for every scene
        - Prefer: charts, trading floor, stock market, business meeting, financial report, economy, investment, money, bankruptcy, lottery, wealth
        - Avoid: abstract concepts, emotions, verbs
        """;

    private const string ImageSelectionSystemPrompt = """
        # Role
        Bạn chọn ảnh nền phù hợp nhất cho từng segment trong video podcast tài chính.

        # Task
        Với mỗi segment, bạn nhận nội dung đoạn văn + danh sách candidate IDs kèm mô tả.
        Chọn 1 ảnh phù hợp nhất dựa trên mô tả text (bạn không nhìn thấy ảnh).

        # Tiêu chí
        - Mô tả ảnh phải liên quan đến nội dung segment
        - Ưu tiên mô tả chứa từ khóa tài chính, kinh doanh, chuyên nghiệp
        - Chỉ dùng các ID có trong danh sách candidates
        - **QUAN TRỌNG: Mỗi segment PHẢI chọn chosenId KHÁC NHAU — KHÔNG được dùng cùng 1 ID cho 2 segment**
        - Nếu nhiều segment có cùng pool candidates, hãy phân bổ đều — mỗi ảnh chỉ dùng 1 lần

        # Output
        Trả về ONLY JSON array — không markdown, không giải thích:
        [{"segmentIndex":0,"chosenId":"pexels:12345","backupIds":["pexels:12346","pixabay:67890"]}]

        backupIds: 2–3 ảnh dự phòng (khác chosenId VÀ khác chosenId của các segment khác, ưu tiên khác provider)
        """;

    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly HttpClient _http;
    private readonly IRuntimeApiSettings _settings;
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public YesScaleProvider(IRuntimeApiSettings settings, HttpClient? httpClient = null)
    {
        _http = httpClient ?? _sharedHttp;
        _settings = settings;
    }

    public YesScaleProvider(AIAnalysisSettings settings, HttpClient? httpClient = null)
        : this(new LegacyRuntimeApiSettings(ai: settings), httpClient)
    {
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        string apiKey,
        string? baseUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<string>();

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl ?? _settings.YesScaleBaseUrl);
        var models = new List<string>();

        var firstPage = await FetchModelIdsAsync($"{normalizedBaseUrl}/models?limit=1000", apiKey, ct);
        models.AddRange(firstPage);

        if (firstPage.Count == 20)
        {
            for (var page = 2; page <= 5; page++)
            {
                var pageItems = await FetchModelIdsAsync($"{normalizedBaseUrl}/models?page={page}", apiKey, ct);
                if (pageItems.Count == 0) break;

                models.AddRange(pageItems);
                if (pageItems.Count < 20) break;
            }
        }

        return models
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> NormalizeScriptAsync(string script, CancellationToken ct = default)
    {
        const string sysPrompt = "You are a transcript correction assistant. Fix speech recognition errors only. Return the corrected transcript with timestamps unchanged.";
        const int maxTokens = 4096;

        try
        {
            // For short/clean scripts, skip normalization entirely — marginal benefit vs latency cost.
            if (script.Length < 500)
                return script;

            var model = _settings.YesScaleModel;
            var sysTokens = TokenEstimator.Estimate(sysPrompt);

            // Chunk if the script is too large for a single normalize call.
            var chunks = ScriptChunker.ChunkScript(script, sysTokens, maxTokens, model, overlapSegments: 0);

            if (chunks.Count <= 1)
            {
                var prompt = $"Normalize the following timestamped transcript. Fix ASR errors. Keep timestamps exactly as-is.\n\n{script}";
                var timeout = ComputeTimeout(prompt, baseSeconds: 60);
                var result = await CallChatWithFallbackAsync(sysPrompt, prompt, model, 0.3, maxTokens, timeout, 1, ct);
                return result.Content;
            }

            // Process chunks in parallel (normalization chunks are independent).
            var tasks = chunks.Select(async chunk =>
            {
                var prompt = $"Normalize the following timestamped transcript. Fix ASR errors. Keep timestamps exactly as-is.\n\n{chunk}";
                var timeout = ComputeTimeout(prompt, baseSeconds: 60);
                return await CallChatWithFallbackAsync(sysPrompt, prompt, model, 0.3, maxTokens, timeout, 1, ct);
            });
            var results = await Task.WhenAll(tasks);
            return string.Join("\n", results.Select(r => r.Content));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NormalizeScriptAsync timed out or failed — using original script");
            return script; // graceful fallback
        }
    }

    public async Task<AIAnalysisResponse> AnalyzeScriptAsync(AIAnalysisRequest request, CancellationToken ct = default)
    {
        var systemPrompt = request.SystemPrompt ?? ScriptAnalysisSystemPrompt;
        var model = request.Model ?? _settings.YesScaleModel;
        var sysTokens = TokenEstimator.Estimate(systemPrompt);
        var maxOutputTokens = request.MaxTokens;

        // Use lower temperature (0.3) for more deterministic structured JSON output.
        var temperature = 0.3;

        var chunks = ScriptChunker.ChunkScript(
            request.Script, sysTokens, maxOutputTokens, model, overlapSegments: 1,
            maxSegmentsPerChunk: 90);

        Log.Information("[AI-ANALYZE] model={Model} chunks={Chunks} inputSegments={InputSegs}",
            model, chunks.Count, ScriptChunker.SplitIntoSegmentLines(request.Script).Count);

        var allSegments = new List<AISegment>();
        var allRaw = new StringBuilder();

        // Process chunks in parallel — each chunk covers a different timestamp range
        // and deduplication by startTime handles the overlap segments.
        var sem = new SemaphoreSlim(3); // limit concurrent API calls
        var chunkTasks = chunks.Select(async (chunk, idx) =>
        {
            await sem.WaitAsync(ct);
            var chunkSw = Stopwatch.StartNew();
            try
            {
                var userPrompt = BuildScriptAnalysisUserPrompt(chunk, request.AudioDuration);
                var timeoutSec = ComputeTimeout(userPrompt, baseSeconds: 90);
                Log.Information("[AI-ANALYZE] chunk {Idx}/{Total} → sending ({Tokens} tokens, timeout={Timeout}s)",
                    idx + 1, chunks.Count, TokenEstimator.Estimate(userPrompt), timeoutSec);

                var result = await CallChatWithFallbackAsync(
                    systemPrompt, userPrompt, model,
                    temperature, maxOutputTokens, timeoutSec, 2, ct);

                chunkSw.Stop();
                if (result.FinishReason == "length")
                    Log.Warning("[AI-ANALYZE] chunk {Idx} TRUNCATED (finish_reason=length) in {Elapsed}s", idx + 1, chunkSw.Elapsed.TotalSeconds.ToString("F1"));
                else
                    Log.Information("[AI-ANALYZE] chunk {Idx}/{Total} ✔ — {Chars} chars in {Elapsed}s",
                        idx + 1, chunks.Count, result.Content.Length, chunkSw.Elapsed.TotalSeconds.ToString("F1"));

                return result;
            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        var chunkResults = await Task.WhenAll(chunkTasks);
        foreach (var result in chunkResults)
        {
            allRaw.AppendLine(result.Content);
            var parsed = ParseAnalysisResponseBestEffort(result.Content);
            allSegments.AddRange(parsed);
        }

        // Deduplicate overlapping segments (from chunk overlap) by startTime.
        var deduplicated = allSegments
            .GroupBy(s => Math.Round(s.StartTime, 2))
            .Select(g => g.First())
            .OrderBy(s => s.StartTime)
            .ToArray();

        // Post-process: split segments longer than MaxSceneDuration.
        var scriptLines = ScriptChunker.ParseTimestampedLines(request.Script);
        var postProcessed = SplitLongSegments(deduplicated, scriptLines);

        // Post-process: fill gaps between consecutive segments.
        postProcessed = FillGaps(postProcessed, scriptLines);

        var inputSegmentCount = ScriptChunker.SplitIntoSegmentLines(request.Script).Count;
        Log.Information("AI returned {Returned} scenes (post-processed from {Raw}) from {InputLines} transcript lines",
            postProcessed.Length, deduplicated.Length, inputSegmentCount);

        return new AIAnalysisResponse(postProcessed, allRaw.ToString());
    }

    private const double MaxSceneDuration = 20.0;
    private const double MinSceneDuration = 6.0;
    private const double TargetSceneDuration = 12.0;
    private const double GapThreshold = 2.0; // fill gaps larger than 2s

    /// <summary>
    /// Split any AI segment longer than MaxSceneDuration into sub-segments
    /// using original transcript line boundaries.
    /// </summary>
    private static AISegment[] SplitLongSegments(
        AISegment[] segments,
        List<(double Start, double End, string Text)> scriptLines)
    {
        var result = new List<AISegment>();
        foreach (var seg in segments)
        {
            var duration = seg.EndTime - seg.StartTime;
            if (duration <= MaxSceneDuration)
            {
                result.Add(seg);
                continue;
            }

            // Find all script lines that fall within this segment
            var linesInSeg = scriptLines
                .Where(l => l.Start >= seg.StartTime - 0.5 && l.End <= seg.EndTime + 0.5)
                .OrderBy(l => l.Start)
                .ToList();

            if (linesInSeg.Count <= 1)
            {
                // Can't split further, keep as-is
                result.Add(seg);
                continue;
            }

            // Group lines into sub-segments of ~TargetSceneDuration
            var subStart = linesInSeg[0].Start;
            var textAccum = new List<string>();
            for (int i = 0; i < linesInSeg.Count; i++)
            {
                textAccum.Add(linesInSeg[i].Text);
                var subEnd = linesInSeg[i].End;
                var subDuration = subEnd - subStart;
                var isLast = i == linesInSeg.Count - 1;

                // Create a sub-segment when we've accumulated enough duration,
                // or when we're at the last line.
                if (subDuration >= TargetSceneDuration - 1.0 || isLast)
                {
                    result.Add(new AISegment(
                        StartTime: Math.Round(subStart, 2),
                        EndTime: Math.Round(subEnd, 2),
                        Text: string.Join(" ", textAccum),
                        Keywords: seg.Keywords // inherit parent's keywords
                    ));
                    if (!isLast)
                    {
                        subStart = linesInSeg[i + 1].Start;
                        textAccum.Clear();
                    }
                }
            }

            Log.Information("[AI-SPLIT] segment {Start:F1}→{End:F1} ({Duration:F1}s) split into {Count} sub-segments",
                seg.StartTime, seg.EndTime, duration, result.Count - result.IndexOf(result[^1]));
        }
        return [.. result];
    }

    /// <summary>
    /// Fill gaps between consecutive segments using original transcript lines.
    /// </summary>
    private static AISegment[] FillGaps(
        AISegment[] segments,
        List<(double Start, double End, string Text)> scriptLines)
    {
        if (segments.Length == 0) return segments;

        var result = new List<AISegment>();

        // Fill gap before the first segment (if script starts earlier)
        if (scriptLines.Count > 0 && scriptLines[0].Start < segments[0].StartTime - GapThreshold)
        {
            var gapLines = scriptLines
                .Where(l => l.End <= segments[0].StartTime + 0.5)
                .OrderBy(l => l.Start)
                .ToList();
            if (gapLines.Count > 0)
            {
                var fillers = CreateFillSegments(gapLines);
                result.AddRange(fillers);
                Log.Information("[AI-FILL] filled leading gap 0→{End:F1}s with {Count} segments",
                    segments[0].StartTime, fillers.Length);
            }
        }

        for (int i = 0; i < segments.Length; i++)
        {
            result.Add(segments[i]);

            if (i < segments.Length - 1)
            {
                var gap = segments[i + 1].StartTime - segments[i].EndTime;
                if (gap > GapThreshold)
                {
                    var gapLines = scriptLines
                        .Where(l => l.Start >= segments[i].EndTime - 0.5 &&
                                    l.End <= segments[i + 1].StartTime + 0.5)
                        .OrderBy(l => l.Start)
                        .ToList();
                    if (gapLines.Count > 0)
                    {
                        var fillers = CreateFillSegments(gapLines);
                        result.AddRange(fillers);
                        Log.Information("[AI-FILL] filled gap {Start:F1}→{End:F1}s ({Gap:F1}s) with {Count} segments",
                            segments[i].EndTime, segments[i + 1].StartTime, gap, fillers.Length);
                    }
                }
            }
        }

        // Fill gap after the last segment
        if (scriptLines.Count > 0 && scriptLines[^1].End > segments[^1].EndTime + GapThreshold)
        {
            var gapLines = scriptLines
                .Where(l => l.Start >= segments[^1].EndTime - 0.5)
                .OrderBy(l => l.Start)
                .ToList();
            if (gapLines.Count > 0)
            {
                var fillers = CreateFillSegments(gapLines);
                result.AddRange(fillers);
                Log.Information("[AI-FILL] filled trailing gap {Start:F1}→end with {Count} segments",
                    segments[^1].EndTime, fillers.Length);
            }
        }

        return [.. result.OrderBy(s => s.StartTime)];
    }

    /// <summary>
    /// Create fill segments from transcript lines, grouping into ~TargetSceneDuration each.
    /// </summary>
    private static AISegment[] CreateFillSegments(List<(double Start, double End, string Text)> lines)
    {
        if (lines.Count == 0) return [];

        var result = new List<AISegment>();
        var subStart = lines[0].Start;
        var textAccum = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            textAccum.Add(lines[i].Text);
            var subEnd = lines[i].End;
            var subDuration = subEnd - subStart;
            var isLast = i == lines.Count - 1;

            if (subDuration >= TargetSceneDuration - 1.0 || isLast)
            {
                result.Add(new AISegment(
                    StartTime: Math.Round(subStart, 2),
                    EndTime: Math.Round(subEnd, 2),
                    Text: string.Join(" ", textAccum),
                    Keywords: ["finance", "investment", "business", "economy", "podcast"]
                ));
                if (!isLast)
                {
                    subStart = lines[i + 1].Start;
                    textAccum.Clear();
                }
            }
        }

        return [.. result];
    }

    public async Task<AIImageSelectionResultItem[]> SelectImagesAsync(
        SegmentWithCandidates[] segments, CancellationToken ct = default)
    {
        var userPrompt = BuildImageSelectionUserPrompt(segments);
        Log.Information("[AI-IMGSEL] selecting images for {Count} segments", segments.Length);
        var sw = Stopwatch.StartNew();
        var result = await CallChatWithFallbackAsync(
            ImageSelectionSystemPrompt, userPrompt, _settings.YesScaleModel,
            0.3, 2048, 90, 2, ct);
        sw.Stop();
        var parsed = ParseImageSelectionResponse(result.Content, segments);
        Log.Information("[AI-IMGSEL] ✔ got {Selected}/{Total} selections in {Elapsed}s",
            parsed.Length, segments.Length, sw.Elapsed.TotalSeconds.ToString("F1"));
        return parsed;
    }

    public async Task<bool> ValidateApiKeyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var req = new HttpRequestMessage(HttpMethod.Get, $"{NormalizeBaseUrl(_settings.YesScaleBaseUrl)}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.YesScaleApiKey);
            var resp = await _http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Prompt builders ──────────────────────────────────────────────────────

    private static string BuildScriptAnalysisUserPrompt(string script, double? audioDuration)
    {
        var sb = new StringBuilder();

        // Calculate chunk-specific duration from timestamps in the chunk.
        // This avoids sending the full audio duration to each chunk (which causes
        // the AI to create too few scenes when the chunk spans more time than audioDuration).
        var chunkLines = ScriptChunker.ParseTimestampedLines(script);
        double chunkDuration;
        if (chunkLines.Count >= 2)
            chunkDuration = chunkLines[^1].End - chunkLines[0].Start;
        else if (audioDuration.HasValue)
            chunkDuration = audioDuration.Value;
        else
            chunkDuration = chunkLines.Count * 3.0; // rough fallback

        var targetScenes = Math.Max(3, (int)Math.Round(chunkDuration / 12.0));
        sb.AppendLine($"This chunk spans {chunkDuration:F1}s of audio ({chunkLines.Count} transcript lines).");
        sb.AppendLine($"Target: ~{targetScenes} scenes (each 10-15s). Merge adjacent lines by topic.");
        sb.AppendLine();
        sb.Append(script);
        return sb.ToString();
    }

    private static string BuildImageSelectionUserPrompt(SegmentWithCandidates[] segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chọn ảnh cho {segments.Length} segment:");

        foreach (var s in segments)
        {
            sb.AppendLine($"\n[{s.SegmentIndex}] \"{s.Context}\"");
            foreach (var c in s.Candidates)
                sb.AppendLine($"  {c.Id}: {c.Semantic}");
        }

        return sb.ToString();
    }

    // ── HTTP + retry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if a model is currently considered dead (failed recently).
    /// Models are automatically revived after <see cref="DeadModelCooldown"/>.
    /// </summary>
    private static bool IsModelDead(string model)
    {
        if (_deadModels.TryGetValue(model, out var deadSince))
        {
            if (DateTimeOffset.UtcNow - deadSince < DeadModelCooldown)
                return true;
            // Cooldown expired — give it another chance.
            _deadModels.TryRemove(model, out _);
        }
        return false;
    }

    private static void MarkModelDead(string model)
    {
        _deadModels[model] = DateTimeOffset.UtcNow;
        Log.Warning("[AI-HEALTH] Model {Model} marked dead — will skip for {Cooldown}m", model, DeadModelCooldown.TotalMinutes);
    }

    private static void MarkModelAlive(string model)
    {
        if (_deadModels.TryRemove(model, out _))
            Log.Information("[AI-HEALTH] Model {Model} recovered — marking alive", model);
    }

    /// <summary>
    /// Run a lightweight health check against each model in the fallback chain.
    /// Dead models are pre-marked so the fallback chain skips them instantly.
    /// </summary>
    public async Task RunModelHealthCheckAsync(CancellationToken ct = default)
    {
        var primaryApiKey = _settings.ResolveApiKey(_settings.PrimaryProfileId);
        var models = new List<(string Model, string ApiKey)> { (_settings.YesScaleModel, primaryApiKey) };

        var fallbacks = _settings.YesScaleFallbackEntries;
        if (fallbacks?.Count > 0)
        {
            foreach (var entry in fallbacks)
            {
                if (!string.IsNullOrWhiteSpace(entry.ModelId))
                    models.Add((entry.ModelId, _settings.ResolveApiKey(entry.ProfileId)));
            }
        }

        Log.Information("[AI-HEALTH] Starting health check for {Count} models...", models.Count);
        var tasks = models.Select(async m =>
        {
            try
            {
                var result = await CallChatAsync(
                    "Reply OK", "test", m.Model, 0.1, 10, 15, m.ApiKey, ct);
                MarkModelAlive(m.Model);
                Log.Information("[AI-HEALTH] ✔ {Model} — healthy", m.Model);
                return (m.Model, Healthy: true);
            }
            catch (Exception ex)
            {
                MarkModelDead(m.Model);
                Log.Warning("[AI-HEALTH] ✘ {Model} — {Error}", m.Model, ex.Message.Length > 100 ? ex.Message[..100] : ex.Message);
                return (m.Model, Healthy: false);
            }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        var healthy = results.Count(r => r.Healthy);
        Log.Information("[AI-HEALTH] Health check complete: {Healthy}/{Total} models healthy", healthy, results.Length);
    }

    /// <summary>
    /// Builds the ordered list of (model, apiKey) attempts, skipping models
    /// currently marked dead. The primary model always comes first if alive.
    /// </summary>
    private List<(string Model, string ApiKey)> BuildAttemptList(string primaryModel)
    {
        var primaryApiKey = _settings.ResolveApiKey(_settings.PrimaryProfileId);
        var attempts = new List<(string Model, string ApiKey)>();

        // Add primary only if alive
        if (!IsModelDead(primaryModel))
            attempts.Add((primaryModel, primaryApiKey));

        var fallbacks = _settings.YesScaleFallbackEntries;
        if (fallbacks?.Count > 0)
        {
            foreach (var entry in fallbacks)
            {
                if (!string.IsNullOrWhiteSpace(entry.ModelId) &&
                    !entry.ModelId.Equals(primaryModel, StringComparison.OrdinalIgnoreCase) &&
                    !IsModelDead(entry.ModelId))
                {
                    var apiKey = _settings.ResolveApiKey(entry.ProfileId);
                    attempts.Add((entry.ModelId, apiKey));
                }
            }
        }

        // If ALL models are dead, reset and try everything (last resort).
        if (attempts.Count == 0)
        {
            Log.Warning("[AI-HEALTH] All models dead — resetting cooldowns for last-resort attempt");
            _deadModels.Clear();
            attempts.Add((primaryModel, primaryApiKey));
            if (fallbacks?.Count > 0)
            {
                foreach (var entry in fallbacks)
                {
                    if (!string.IsNullOrWhiteSpace(entry.ModelId) &&
                        !entry.ModelId.Equals(primaryModel, StringComparison.OrdinalIgnoreCase))
                    {
                        attempts.Add((entry.ModelId, _settings.ResolveApiKey(entry.ProfileId)));
                    }
                }
            }
        }

        return attempts;
    }

    /// <summary>
    /// Picks a model from the attempt list using round-robin distribution.
    /// Returns the reordered list starting from the picked model, preserving
    /// fallback order for the rest. This spreads concurrent requests across
    /// multiple models to prevent rate-limiting any single one.
    /// </summary>
    private List<(string Model, string ApiKey)> DistributeAttemptList(List<(string Model, string ApiKey)> attempts)
    {
        if (attempts.Count <= 1) return attempts;

        var startIdx = Interlocked.Increment(ref _roundRobinCounter) % attempts.Count;
        if (startIdx < 0) startIdx += attempts.Count; // handle negative wrap

        var reordered = new List<(string Model, string ApiKey)>(attempts.Count);
        for (int i = 0; i < attempts.Count; i++)
            reordered.Add(attempts[(startIdx + i) % attempts.Count]);
        return reordered;
    }

    /// <summary>
    /// Calls the chat completion API with fallback models and hedged requests.
    /// 
    /// Strategy:
    /// 1. Build attempt list skipping dead models (from health cache).
    /// 2. Distribute via round-robin to spread load across models.
    /// 3. For each model: start primary request. If no response within HedgeDelay,
    ///    fire a "hedge" request to the next model in parallel. First response wins.
    /// 4. On transient failure, mark model dead and try next fallback.
    /// </summary>
    private async Task<ChatResult> CallChatWithFallbackAsync(
        string systemPrompt, string userPrompt, string primaryModel,
        double temperature, int maxTokens, int timeoutSeconds,
        int maxRetries, CancellationToken ct)
    {
        var attempts = BuildAttemptList(primaryModel);
        attempts = DistributeAttemptList(attempts);

        Exception? lastException = null;
        for (int i = 0; i < attempts.Count; i++)
        {
            var (model, apiKey) = attempts[i];
            try
            {
                // Start the primary request
                var primaryTask = FetchWithRetryAsync(
                    () => CallChatAsync(systemPrompt, userPrompt, model,
                                        temperature, maxTokens, timeoutSeconds, apiKey, ct),
                    maxRetries, ct);

                // Hedged request: if primary takes longer than HedgeDelay and we have
                // more models available, fire a parallel request to the next model.
                // Whichever finishes first wins; the other is abandoned (CTS cancelled).
                if (i < attempts.Count - 1)
                {
                    var result = await TryWithHedgeAsync(
                        primaryTask, systemPrompt, userPrompt,
                        attempts[i + 1].Model, attempts[i + 1].ApiKey,
                        temperature, maxTokens, timeoutSeconds, maxRetries, ct);

                    if (result.UsedHedge)
                    {
                        // Hedge won — the primary model was too slow; mark it for awareness
                        Log.Information("[AI-HEDGE] Hedge model {HedgeModel} beat primary {PrimaryModel}",
                            attempts[i + 1].Model, model);
                        i++; // skip the next model since we already used it as hedge
                    }

                    MarkModelAlive(result.UsedHedge ? attempts[i].Model : model);
                    if (!result.UsedHedge && i > 0)
                        Log.Information("Fallback model {Model} succeeded (primary was {Primary})", model, primaryModel);

                    return result.Result;
                }

                // Last model — no hedge available, just await directly
                var directResult = await primaryTask;
                MarkModelAlive(model);
                if (i > 0)
                    Log.Information("Fallback model {Model} succeeded (primary was {Primary})", model, primaryModel);
                return directResult;
            }
            catch (ApiException aex) when ((aex.IsTransient || aex.IsChannelError) && i < attempts.Count - 1)
            {
                MarkModelDead(model);
                if (aex.IsChannelError)
                    Log.Warning("Model {Model} not available for this API key, trying fallback {Next}",
                        model, attempts[i + 1].Model);
                else
                    Log.Warning("Model {Model} failed with transient error {StatusCode}, trying fallback {Next}",
                        model, aex.StatusCode, attempts[i + 1].Model);
                lastException = aex;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && i < attempts.Count - 1)
            {
                MarkModelDead(model);
                Log.Warning("Model {Model} timed out, trying fallback {Next}",
                    model, attempts[i + 1].Model);
            }
        }

        if (lastException != null)
            throw lastException;
        throw new InvalidOperationException("All models failed");
    }

    /// <summary>
    /// Races the primary task against a hedged request to a different model.
    /// If the primary completes within HedgeDelay, returns immediately.
    /// Otherwise fires a hedge request and returns whichever finishes first.
    /// </summary>
    private async Task<HedgeResult> TryWithHedgeAsync(
        Task<ChatResult> primaryTask,
        string systemPrompt, string userPrompt,
        string hedgeModel, string hedgeApiKey,
        double temperature, int maxTokens, int timeoutSeconds,
        int maxRetries, CancellationToken ct)
    {
        // Wait for the primary to either complete or for the hedge delay to expire
        var hedgeDelayTask = Task.Delay(HedgeDelay, ct);
        var firstDone = await Task.WhenAny(primaryTask, hedgeDelayTask);

        if (firstDone == primaryTask)
        {
            // Primary finished before hedge delay — use it directly
            var result = await primaryTask; // re-await to propagate exceptions
            return new HedgeResult(result, UsedHedge: false);
        }

        // Primary is slow — fire a hedge request to the next model
        Log.Information("[AI-HEDGE] Primary slow after {Delay}s — hedging with {HedgeModel}",
            HedgeDelay.TotalSeconds, hedgeModel);

        var hedgeTask = FetchWithRetryAsync(
            () => CallChatAsync(systemPrompt, userPrompt, hedgeModel,
                                temperature, maxTokens, timeoutSeconds, hedgeApiKey, ct),
            maxRetries, ct);

        // Race: whoever finishes first wins
        var winner = await Task.WhenAny(primaryTask, hedgeTask);

        if (winner == primaryTask)
        {
            try
            {
                var result = await primaryTask;
                return new HedgeResult(result, UsedHedge: false);
            }
            catch
            {
                // Primary failed after hedge was started — wait for hedge
                return new HedgeResult(await hedgeTask, UsedHedge: true);
            }
        }
        else
        {
            try
            {
                var result = await hedgeTask;
                return new HedgeResult(result, UsedHedge: true);
            }
            catch
            {
                // Hedge failed — fall back to primary
                return new HedgeResult(await primaryTask, UsedHedge: false);
            }
        }
    }

    private sealed record HedgeResult(ChatResult Result, bool UsedHedge);

    private async Task<ChatResult> CallChatAsync(
        string systemPrompt, string userPrompt, string model,
        double temperature, int maxTokens, int timeoutSeconds,
        string? apiKeyOverride, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // Log full prompt to ai-prompts-*.txt for analysis
        var aiLog = Log.ForContext("AICall", true);
        aiLog.Debug("[PROMPT] model={Model} temp={Temp} maxTokens={MaxTokens}{NewLine}" +
                    "=== SYSTEM ===\n{System}\n=== USER ===\n{User}",
            model, temperature, maxTokens, Environment.NewLine, systemPrompt, userPrompt);

        var body = new
        {
            model,
            temperature,
            max_tokens = maxTokens,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            }
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(_settings.YesScaleBaseUrl)}/chat/completions")
        {
            Content = content
        };
        var effectiveKey = apiKeyOverride ?? _settings.YesScaleApiKey;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveKey);

        var httpSw = Stopwatch.StartNew();
        var response = await _http.SendAsync(request, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
        httpSw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[AI-HTTP] {Model} \u2717 HTTP {Status} in {Elapsed}s — {Body}",
                model, (int)response.StatusCode, httpSw.Elapsed.TotalSeconds.ToString("F1"),
                responseBody.Length > 200 ? responseBody[..200] : responseBody);
            aiLog.Debug("[RESPONSE-ERROR] model={Model} HTTP {Status} in {Elapsed}s{NewLine}=== ERROR BODY ===\n{Body}",
                model, (int)response.StatusCode, httpSw.Elapsed.TotalSeconds.ToString("F1"), Environment.NewLine, responseBody);
            throw new ApiException((int)response.StatusCode,
                $"YesScale API returned {(int)response.StatusCode}: {responseBody}");
        }

        Log.Information("[AI-HTTP] {Model} \u2714 HTTP {Status} in {Elapsed}s ({ResponseLen} chars)",
            model, (int)response.StatusCode, httpSw.Elapsed.TotalSeconds.ToString("F1"), responseBody.Length);

        var node = JsonNode.Parse(responseBody);
        var text = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("Unexpected API response shape");
        var finishReason = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "unknown";

        // Log raw response to ai-prompts-*.txt for analysis
        aiLog.Debug("[RESPONSE] model={Model} finish={Finish} elapsed={Elapsed}s{NewLine}=== RESPONSE ===\n{Response}",
            model, finishReason, httpSw.Elapsed.TotalSeconds.ToString("F1"), Environment.NewLine, text);

        return new ChatResult(text, finishReason);
    }

    /// <summary>
    /// Compute dynamic timeout: base + proportional to input size.
    /// Longer prompts need more time for the model to process.
    /// </summary>
    private static int ComputeTimeout(string userPrompt, int baseSeconds)
    {
        var estimatedTokens = TokenEstimator.Estimate(userPrompt);
        // ~5 extra seconds per 1000 tokens, capped at 5 minutes total.
        var extra = (int)(estimatedTokens / 1000.0 * 5);
        return Math.Min(baseSeconds + extra, 300);
    }

    private static async Task<ChatResult> FetchWithRetryAsync(
        Func<Task<ChatResult>> action, int maxRetries, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ApiException aex) when (aex.IsChannelError)
            {
                // Channel not available for this API key — will never succeed on retry, fall through to next model immediately.
                throw;
            }
            catch (ApiException aex) when (!aex.IsTransient)
            {
                // Permanent error (400, 401, 403, 404) — fail immediately, do not retry.
                Log.Error("AI call failed with non-retryable status {StatusCode}: {Message}", aex.StatusCode, aex.Message);
                throw;
            }
            catch (ApiException aex) when (aex.StatusCode == 429 && attempt < maxRetries)
            {
                // Rate limited — use longer backoff.
                var rateLimitDelay = delay * 3;
                Log.Warning("AI call rate-limited (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt + 1, maxRetries + 1, rateLimitDelay.TotalSeconds);
                await Task.Delay(rateLimitDelay, ct);
                delay *= 2;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                Log.Warning(ex, "AI call failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt + 1, maxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay *= 2; // exponential backoff
            }
        }
        // last attempt — let it throw
        return await action();
    }

    private async Task<IReadOnlyList<string>> FetchModelIdsAsync(string url, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"YesScale models API returned {(int)response.StatusCode}: {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return data.EnumerateArray()
            .Select(static item => item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToArray();
    }

    private static string NormalizeBaseUrl(string baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.yescale.vip/v1"
            : baseUrl.Trim().TrimEnd('/');

    // ── Response parsers ─────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort parser: extracts as many valid segments as possible from potentially
    /// truncated or malformed AI responses instead of failing the entire batch.
    /// </summary>
    private static AISegment[] ParseAnalysisResponseBestEffort(string raw)
    {
        var segments = new List<AISegment>();

        // Try the strict path first.
        string json;
        try
        {
            json = ExtractJsonPayload(raw, preferArray: true);
        }
        catch
        {
            // JSON payload couldn't be found/extracted at all.
            // Try to salvage individual JSON objects from the raw text.
            json = raw;
        }

        // Attempt full array deserialization first (fast path).
        try
        {
            var arr = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOpts);
            if (arr != null)
            {
                foreach (var el in arr)
                {
                    var seg = TryParseOneSegment(el);
                    if (seg != null) segments.Add(seg);
                }
                if (segments.Count > 0)
                    return [.. segments];
            }
        }
        catch
        {
            // Full parse failed — fallback to incremental extraction.
        }

        // Incremental: find individual { ... } objects that look like valid segments.
        var idx = 0;
        while (idx < json.Length)
        {
            var objStart = json.IndexOf('{', idx);
            if (objStart < 0) break;

            var objEnd = FindJsonEnd(json[objStart..]);
            if (objEnd < 0) break;

            var objJson = json.Substring(objStart, objEnd + 1);
            idx = objStart + objEnd + 1;

            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(objJson, _jsonOpts);
                var seg = TryParseOneSegment(el);
                if (seg != null) segments.Add(seg);
            }
            catch
            {
                // skip malformed object
            }
        }

        if (segments.Count == 0)
            Log.Warning("ParseAnalysisResponseBestEffort: could not extract any segments from response ({Length} chars)", raw.Length);
        else
            Log.Information("ParseAnalysisResponseBestEffort: extracted {Count} segment(s)", segments.Count);

        return [.. segments];
    }

    /// <summary>
    /// Try to parse a single JsonElement into an AISegment. Returns null on failure.
    /// </summary>
    private static AISegment? TryParseOneSegment(JsonElement el)
    {
        try
        {
            if (!el.TryGetProperty("startTime", out _) || !el.TryGetProperty("endTime", out _))
                return null;

            return new AISegment(
                StartTime: ReadDouble(el, "startTime"),
                EndTime:   ReadDouble(el, "endTime"),
                Text:      el.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                Keywords:  el.TryGetProperty("keywords", out var k)
                             ? k.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Take(5).ToArray()
                             : Array.Empty<string>()
            );
        }
        catch
        {
            return null;
        }
    }

    private static AISegment[] ParseAnalysisResponse(string raw)
    {
        var json = ExtractJsonPayload(raw, preferArray: true);

        try
        {
            var arr = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOpts)
                      ?? throw new InvalidOperationException("Null deserialization result");

            return arr.Select(el => new AISegment(
                StartTime: ReadDouble(el, "startTime"),
                EndTime:   ReadDouble(el, "endTime"),
                Text:      el.GetProperty("text").GetString() ?? string.Empty,
                Keywords:  el.GetProperty("keywords").EnumerateArray()
                             .Select(k => k.GetString() ?? string.Empty)
                             .Take(5).ToArray()
            )).ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse script analysis response: {ex.Message}\nRaw: {raw}", ex);
        }
    }

    private static AIImageSelectionResultItem[] ParseImageSelectionResponse(
        string raw, SegmentWithCandidates[] segments)
    {
        var validIds = segments
            .SelectMany(s => s.Candidates.Select(c => c.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build position→globalIndex map.
        // AI often returns 0-based batch-local indices (0,1,2…) even though
        // the prompt uses global segment indices.  Map both so either works:
        //   position 0 → segments[0].SegmentIndex (global)
        //   position 1 → segments[1].SegmentIndex (global)  etc.
        var globalIndexByPosition = new Dictionary<int, int>();
        var globalIndexSet = new HashSet<int>();
        for (int i = 0; i < segments.Length; i++)
        {
            globalIndexByPosition[i] = segments[i].SegmentIndex;
            globalIndexSet.Add(segments[i].SegmentIndex);
        }

        try
        {
            var json = ExtractJsonPayload(raw, preferArray: true);
            var arr = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOpts)
                      ?? [];

            var results = new List<AIImageSelectionResultItem>();
            foreach (var el in arr)
            {
                var rawIdx  = el.GetProperty("segmentIndex").GetInt32();

                // Determine the correct global segment index:
                // AI models (especially lighter ones) commonly return 0-based
                // batch-local positions even when the prompt shows global indices.
                // Priority:
                //   1) rawIdx < batch size → treat as batch-local position (most common)
                //   2) rawIdx matches a global index in this batch → use as global
                //   3) Otherwise → skip
                int globalIdx;
                if (rawIdx >= 0 && rawIdx < segments.Length)
                    globalIdx = segments[rawIdx].SegmentIndex;
                else if (globalIndexSet.Contains(rawIdx))
                    globalIdx = rawIdx;
                else
                {
                    Log.Warning("AI returned out-of-range segmentIndex {Idx} — skipping", rawIdx);
                    continue;
                }

                var chosen  = el.GetProperty("chosenId").GetString() ?? string.Empty;
                // backupIds and reason are optional — prompt no longer requests them
                var backups = el.TryGetProperty("backupIds", out var bArr)
                               ? bArr.EnumerateArray()
                                     .Select(k => k.GetString() ?? string.Empty)
                                     .Where(id => validIds.Contains(id))
                                     .ToArray()
                               : [];
                var reason  = el.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;

                // Validate chosen ID exists in candidates
                if (!validIds.Contains(chosen))
                {
                    Log.Warning("AI returned invalid chosenId '{Id}' for segment {Idx} — skipping", chosen, globalIdx);
                    continue;
                }

                results.Add(new AIImageSelectionResultItem(globalIdx, chosen, backups, reason));
            }
            return [.. results];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse image selection response ({RawLen} chars) — returning empty. Raw: {Raw}",
                raw.Length, raw.Length > 500 ? raw[..500] : raw);
            return [];
        }
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Property '{propertyName}' is not a valid number")
        };
    }

    private static string ExtractJsonPayload(string text, bool preferArray)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Response was empty");

        var fenced = TryExtractFencedCodeBlock(trimmed);
        if (!string.IsNullOrWhiteSpace(fenced))
            trimmed = fenced.Trim();

        if ((preferArray && trimmed.StartsWith("[")) || (!preferArray && trimmed.StartsWith("{")))
            return trimmed;

        var start = FindJsonStart(trimmed, preferArray);
        if (start < 0)
            throw new InvalidOperationException("Could not find JSON payload in AI response");

        var slice = trimmed[start..];
        var end = FindJsonEnd(slice);
        if (end < 0)
        {
            // Attempt truncation recovery: close unclosed brackets
            var repaired = TryRepairTruncatedJson(slice);
            if (repaired != null)
            {
                Log.Warning("JSON payload was truncated ({Len} chars) — attempted repair", slice.Length);
                return repaired;
            }
            throw new InvalidOperationException("Could not find the end of the JSON payload in AI response");
        }

        return slice[..(end + 1)].Trim();
    }

    /// <summary>
    /// Best-effort repair of truncated JSON by removing the last incomplete element
    /// and closing unclosed brackets/braces.
    /// </summary>
    private static string? TryRepairTruncatedJson(string json)
    {
        // Find the last complete object boundary ("},") and truncate there
        var lastComplete = json.LastIndexOf("},", StringComparison.Ordinal);
        if (lastComplete < 0) return null;

        var truncated = json[..(lastComplete + 1)]; // include the "}"

        // Count unclosed brackets
        int openBrackets = 0, openBraces = 0;
        bool inString = false, escaped = false;
        foreach (var ch in truncated)
        {
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '[') openBrackets++;
            else if (ch == ']') openBrackets--;
            else if (ch == '{') openBraces++;
            else if (ch == '}') openBraces--;
        }

        // Close unclosed brackets
        var sb = new StringBuilder(truncated);
        for (int i = 0; i < openBraces; i++) sb.Append('}');
        for (int i = 0; i < openBrackets; i++) sb.Append(']');

        var repaired = sb.ToString();

        // Validate it's actually parseable
        try
        {
            JsonDocument.Parse(repaired);
            return repaired;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractFencedCodeBlock(string text)
    {
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0) return null;

        var contentStart = text.IndexOf('\n', fenceStart);
        if (contentStart < 0) return null;

        var fenceEnd = text.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
        if (fenceEnd < 0) return null;

        return text[(contentStart + 1)..fenceEnd];
    }

    private static int FindJsonStart(string text, bool preferArray)
    {
        var primary = preferArray ? '[' : '{';
        var secondary = preferArray ? '{' : '[';
        var primaryIndex = text.IndexOf(primary);
        var secondaryIndex = text.IndexOf(secondary);

        if (primaryIndex < 0) return secondaryIndex;
        if (secondaryIndex < 0) return primaryIndex;
        return Math.Min(primaryIndex, secondaryIndex);
    }

    private static int FindJsonEnd(string text)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '[' || ch == '{')
            {
                stack.Push(ch);
                continue;
            }

            if (ch == ']' || ch == '}')
            {
                if (stack.Count == 0)
                    return -1;

                var open = stack.Pop();
                if ((open == '[' && ch != ']') || (open == '{' && ch != '}'))
                    return -1;

                if (stack.Count == 0)
                    return i;
            }
        }

        return -1;
    }
}
