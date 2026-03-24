using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

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
        var prompt = $"Normalize the following timestamped transcript. Fix ASR errors. Keep timestamps exactly as-is.\n\n{script}";
        try
        {
            var result = await CallChatAsync(
                systemPrompt: "You are a transcript correction assistant. Fix speech recognition errors only. Return the corrected transcript with timestamps unchanged.",
                userPrompt: prompt,
                model: _settings.YesScaleModel,
                temperature: 0.3,
                maxTokens: 4096,
                timeoutSeconds: 60,
                ct: ct);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NormalizeScriptAsync timed out or failed — using original script");
            return script; // graceful fallback
        }
    }

    public async Task<AIAnalysisResponse> AnalyzeScriptAsync(AIAnalysisRequest request, CancellationToken ct = default)
    {
        var userPrompt = BuildScriptAnalysisUserPrompt(request.Script, request.AudioDuration);
        var systemPrompt = request.SystemPrompt ?? ScriptAnalysisSystemPrompt;

        var raw = await FetchWithRetryAsync(
            () => CallChatAsync(systemPrompt, userPrompt, request.Model ?? _settings.YesScaleModel,
                                request.Temperature, request.MaxTokens, 90, ct),
            maxRetries: 1,
            ct);

        var segments = ParseAnalysisResponse(raw);
        return new AIAnalysisResponse(segments, raw);
    }

    public async Task<AIImageSelectionResultItem[]> SelectImagesAsync(
        SegmentWithCandidates[] segments, CancellationToken ct = default)
    {
        var userPrompt = BuildImageSelectionUserPrompt(segments);
        var raw = await FetchWithRetryAsync(
            () => CallChatAsync(ImageSelectionSystemPrompt, userPrompt, _settings.YesScaleModel,
                                0.3, 2048, 120, ct),
            maxRetries: 2,
            ct);

        return ParseImageSelectionResponse(raw, segments);
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

    private async Task<string> CallChatAsync(
        string systemPrompt, string userPrompt, string model,
        double temperature, int maxTokens, int timeoutSeconds,
        CancellationToken ct)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.YesScaleApiKey);

        var response = await _http.SendAsync(request, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"YesScale API returned {(int)response.StatusCode}: {responseBody}");

        var node = JsonNode.Parse(responseBody);
        return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Unexpected API response shape");
    }

    private static async Task<string> FetchWithRetryAsync(
        Func<Task<string>> action, int maxRetries, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action();
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
