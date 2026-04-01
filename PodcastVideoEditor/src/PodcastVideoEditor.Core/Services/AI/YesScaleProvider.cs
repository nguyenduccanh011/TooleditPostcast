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
        You are an AI assistant that analyzes podcast transcripts for a financial/investment video editor.

        Your task:
        1. You will receive a timestamped script in the format [start → end] text
        2. Fix obvious ASR (speech recognition) errors, especially Vietnamese names, financial terms, and numbers
        3. For each segment, generate exactly 5 English keywords suitable for stock image search
        4. Keywords must be relevant to the content: financial, business, market themes
        5. Return a JSON array — no markdown, no extra text

        Output format (strict JSON array):
        [
          {
            "startTime": 0.0,
            "endTime": 6.04,
            "text": "corrected transcript text",
            "keywords": ["keyword1", "keyword2", "keyword3", "keyword4", "keyword5"]
          }
        ]

        Rules:
        - Timestamps must be contiguous (each segment's startTime = previous endTime)
        - Preserve all original segments — do not merge or split
        - Keywords must be English, concrete, visually searchable (avoid abstract concepts)
        - Prefer financial/business imagery: charts, trading floors, documents, professionals
        """;

    private const string ImageSelectionSystemPrompt = """
        Bạn là AI chuyên chọn ảnh nền phù hợp cho video podcast tài chính/đầu tư.

        Nhiệm vụ: Với mỗi segment, chọn 1 ảnh chính (chosen) và 2-3 ảnh dự phòng (backups) từ danh sách candidates.

        Tiêu chí chọn ảnh:
        - Phải liên quan đến nội dung đoạn văn
        - Ưu tiên ảnh có tính chuyên nghiệp, phù hợp video tài chính
        - Tránh ảnh có watermark, logo, text overlay
        - Ưu tiên ảnh portrait/vertical orientation

        Output format (strict JSON array, không có markdown):
        [
          {
            "segmentIndex": 0,
            "chosenId": "pexels:12345",
            "backupIds": ["pixabay:67890", "unsplash:11111"],
            "reason": "Lý do ngắn gọn tại sao chọn ảnh này"
          }
        ]

        Lưu ý: chỉ dùng các ID có trong danh sách candidates được cung cấp.
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
            request.Script, sysTokens, maxOutputTokens, model, overlapSegments: 1);

        Log.Information("AnalyzeScriptAsync: script split into {ChunkCount} chunk(s) for model {Model}",
            chunks.Count, model);

        var allSegments = new List<AISegment>();
        var allRaw = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var userPrompt = BuildScriptAnalysisUserPrompt(chunk, request.AudioDuration);
            var timeoutSec = ComputeTimeout(userPrompt, baseSeconds: 90);

            var result = await CallChatWithFallbackAsync(
                systemPrompt, userPrompt, model,
                temperature, maxOutputTokens, timeoutSec, 2, ct);

            if (result.FinishReason == "length")
                Log.Warning("AI output was truncated (finish_reason=length) for a script chunk — some segments may be missing");

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

        // Validate segment count vs input segment count.
        var inputSegmentCount = ScriptChunker.SplitIntoSegmentLines(request.Script).Count;
        if (deduplicated.Length < inputSegmentCount)
            Log.Warning("AI returned {Returned} segments but input had {Expected} — {Missing} segment(s) missing",
                deduplicated.Length, inputSegmentCount, inputSegmentCount - deduplicated.Length);

        return new AIAnalysisResponse(deduplicated, allRaw.ToString());
    }

    public async Task<AIImageSelectionResultItem[]> SelectImagesAsync(
        SegmentWithCandidates[] segments, CancellationToken ct = default)
    {
        var userPrompt = BuildImageSelectionUserPrompt(segments);
        var result = await CallChatWithFallbackAsync(
            ImageSelectionSystemPrompt, userPrompt, _settings.YesScaleModel,
            0.3, 2048, 120, 2, ct);

        return ParseImageSelectionResponse(result.Content, segments);
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
        var durationNote = audioDuration.HasValue
            ? $"Total audio duration: {audioDuration:F2} seconds.\n"
            : string.Empty;
        return $"{durationNote}Analyze this timestamped podcast transcript and return the JSON array:\n\n{script}";
    }

    private static string BuildImageSelectionUserPrompt(SegmentWithCandidates[] segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Chọn ảnh nền cho các segment sau:");
        sb.AppendLine();

        foreach (var s in segments)
        {
            sb.AppendLine($"Segment {s.SegmentIndex}: \"{s.Context}\"");
            sb.AppendLine("Candidates:");
            foreach (var c in s.Candidates)
                sb.AppendLine($"  - {c.Id}: {c.Semantic}");
            sb.AppendLine();
        }

        sb.AppendLine("Trả về JSON array theo đúng format đã mô tả.");
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
            catch (ApiException aex) when (aex.IsTransient && i < attempts.Count - 1)
            {
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

        var response = await _http.SendAsync(request, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode,
                $"YesScale API returned {(int)response.StatusCode}: {responseBody}");

        var node = JsonNode.Parse(responseBody);
        var text = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("Unexpected API response shape");
        var finishReason = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "unknown";

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
        var json = ExtractJsonPayload(raw, preferArray: true);
        var validIds = segments
            .SelectMany(s => s.Candidates.Select(c => c.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var arr = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOpts)
                      ?? [];

            var results = new List<AIImageSelectionResultItem>();
            foreach (var el in arr)
            {
                var idx     = el.GetProperty("segmentIndex").GetInt32();
                var chosen  = el.GetProperty("chosenId").GetString() ?? string.Empty;
                var backups = el.GetProperty("backupIds").EnumerateArray()
                               .Select(k => k.GetString() ?? string.Empty)
                               .Where(id => validIds.Contains(id))
                               .ToArray();
                var reason  = el.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;

                // Validate chosen ID exists in candidates
                if (!validIds.Contains(chosen))
                {
                    Log.Warning("AI returned invalid chosenId '{Id}' for segment {Idx} — skipping", chosen, idx);
                    continue;
                }

                results.Add(new AIImageSelectionResultItem(idx, chosen, backups, reason));
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
