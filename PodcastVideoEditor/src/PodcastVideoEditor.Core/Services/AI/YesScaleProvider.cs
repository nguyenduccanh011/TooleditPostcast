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
        5. Generate exactly 3 English keywords per scene for stock image search

        # Scene merging rules
        - Target duration: 10–15 seconds per scene. Minimum 6s, maximum 20s
        - Group lines by topic continuity — when the speaker changes subject, start a new scene
        - startTime = first line's start, endTime = last line's end
        - Combine all text from merged lines into one "text" field (keep Vietnamese)
        - Do NOT create 1-line scenes unless a single line is already ≥10s

        # Output
        Return ONLY a JSON array — no markdown fences, no explanation:
        [{"startTime":0.0,"endTime":12.38,"text":"combined corrected text of all merged lines","keywords":["kw1","kw2","kw3"]}]

        # Keyword rules
        - English only, concrete, visually searchable nouns/phrases
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

        # Output
        Trả về ONLY JSON array — không markdown, không giải thích:
        [{"segmentIndex":0,"chosenId":"pexels:12345"}]
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
            maxSegmentsPerChunk: 60);

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

        // Validate: scenes should be fewer than input lines (merged) but non-zero.
        var inputSegmentCount = ScriptChunker.SplitIntoSegmentLines(request.Script).Count;
        Log.Information("AI returned {Returned} scenes from {InputLines} transcript lines",
            deduplicated.Length, inputSegmentCount);

        return new AIAnalysisResponse(deduplicated, allRaw.ToString());
    }

    public async Task<AIImageSelectionResultItem[]> SelectImagesAsync(
        SegmentWithCandidates[] segments, CancellationToken ct = default)
    {
        var userPrompt = BuildImageSelectionUserPrompt(segments);
        Log.Information("[AI-IMGSEL] selecting images for {Count} segments", segments.Length);
        var sw = Stopwatch.StartNew();
        var result = await CallChatWithFallbackAsync(
            ImageSelectionSystemPrompt, userPrompt, _settings.YesScaleModel,
            0.3, 1024, 90, 2, ct);
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
        if (audioDuration.HasValue)
        {
            sb.AppendLine($"Audio duration: {audioDuration:F1}s");
            var targetScenes = Math.Max(3, (int)Math.Round(audioDuration.Value / 12.0));
            sb.AppendLine($"Target: ~{targetScenes} scenes (each 10-15s). Merge adjacent lines by topic.");
        }
        else
        {
            var segCount = ScriptChunker.SplitIntoSegmentLines(script).Count;
            var targetScenes = Math.Max(3, segCount / 4);
            sb.AppendLine($"Input has {segCount} transcript lines. Merge into ~{targetScenes} scenes (each 10-15s, 2-5 lines per scene).");
        }
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
    /// Calls the chat completion API with fallback models.
    /// First tries the primary model with retries. On transient failure, tries each
    /// fallback entry from <see cref="IRuntimeApiSettings.YesScaleFallbackEntries"/> in order,
    /// resolving the API key per entry via its profile.
    /// </summary>
    private async Task<ChatResult> CallChatWithFallbackAsync(
        string systemPrompt, string userPrompt, string primaryModel,
        double temperature, int maxTokens, int timeoutSeconds,
        int maxRetries, CancellationToken ct)
    {
        // Build the ordered attempt list: (model, apiKey)
        var primaryApiKey = _settings.ResolveApiKey(_settings.PrimaryProfileId);
        var attempts = new List<(string Model, string ApiKey)> { (primaryModel, primaryApiKey) };

        var fallbacks = _settings.YesScaleFallbackEntries;
        if (fallbacks?.Count > 0)
        {
            foreach (var entry in fallbacks)
            {
                if (!string.IsNullOrWhiteSpace(entry.ModelId) &&
                    !entry.ModelId.Equals(primaryModel, StringComparison.OrdinalIgnoreCase))
                {
                    var apiKey = _settings.ResolveApiKey(entry.ProfileId);
                    attempts.Add((entry.ModelId, apiKey));
                }
            }
        }

        Exception? lastException = null;
        for (int i = 0; i < attempts.Count; i++)
        {
            var (model, apiKey) = attempts[i];
            try
            {
                var result = await FetchWithRetryAsync(
                    () => CallChatAsync(systemPrompt, userPrompt, model,
                                        temperature, maxTokens, timeoutSeconds, apiKey, ct),
                    maxRetries, ct);

                if (i > 0)
                    Log.Information("Fallback model {Model} succeeded (primary was {Primary})", model, primaryModel);

                return result;
            }
            catch (ApiException aex) when ((aex.IsTransient || aex.IsChannelError) && i < attempts.Count - 1)
            {
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
                Log.Warning("Model {Model} timed out, trying fallback {Next}",
                    model, attempts[i + 1].Model);
            }
        }

        if (lastException != null)
            throw lastException;
        throw new InvalidOperationException("All models failed");
    }

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
                             ? k.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Take(3).ToArray()
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
                             .Take(3).ToArray()
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
        var json = ExtractJsonPayload(raw, preferArray: true);
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
            Log.Warning(ex, "Failed to parse image selection response — returning empty");
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
            throw new InvalidOperationException("Could not find the end of the JSON payload in AI response");

        return slice[..(end + 1)].Trim();
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
