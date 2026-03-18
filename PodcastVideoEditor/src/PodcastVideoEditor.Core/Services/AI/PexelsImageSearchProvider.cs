using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Fetches image candidates from Pexels API.
/// Rate limit: 200 req/hr.
/// </summary>
public sealed class PexelsImageSearchProvider : IImageSearchProvider
{
    public string ProviderName => "pexels";

    // Shared static HttpClient for Pexels.
    private static readonly HttpClient _sharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public PexelsImageSearchProvider(ImageSearchSettings settings, HttpClient? httpClient = null)
    {
        _http = httpClient ?? _sharedHttp;
        _apiKey = settings.PexelsApiKey;
    }

    public async Task<ImageCandidate[]> SearchAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return [];

        try
        {
            var url = $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&per_page={count}&orientation=portrait";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Pexels uses the API key directly in Authorization (not Bearer)
            request.Headers.Add("Authorization", _apiKey);

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            return doc.RootElement.GetProperty("photos").EnumerateArray()
                .Select(photo =>
                {
                    var id = photo.GetProperty("id").GetInt64().ToString();
                    var alt = photo.TryGetProperty("alt", out var a) ? a.GetString() ?? query : query;
                    var src = photo.GetProperty("src");
                    var url = GetBestSourceUrl(src);
                    var thumb = src.TryGetProperty("small", out var s) ? s.GetString() ?? string.Empty : url;
                    return new ImageCandidate($"pexels:{id}", alt, url, thumb, "pexels");
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pexels search failed for query '{Query}'", query);
            return [];
        }
    }

    private static string GetBestSourceUrl(JsonElement src)
    {
        // "large2x" = ~1880px, "large" = ~940px — avoids downloading "original" (4000-6000px) unnecessarily.
        foreach (var propertyName in new[] { "large2x", "large", "medium", "original" })
        {
            if (src.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return string.Empty;
    }
}
