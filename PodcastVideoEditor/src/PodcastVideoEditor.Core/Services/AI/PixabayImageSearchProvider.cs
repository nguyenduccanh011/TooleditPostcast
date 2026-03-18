using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Fetches image candidates from Pixabay API.
/// Rate limit: 5000 req/hr.
/// </summary>
public sealed class PixabayImageSearchProvider : IImageSearchProvider
{
    public string ProviderName => "pixabay";

    // Shared static HttpClient for Pixabay.
    private static readonly HttpClient _sharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public PixabayImageSearchProvider(ImageSearchSettings settings, HttpClient? httpClient = null)
    {
        _http = httpClient ?? _sharedHttp;
        _apiKey = settings.PixabayApiKey;
    }

    public async Task<ImageCandidate[]> SearchAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return [];

        try
        {
            var url = $"https://pixabay.com/api/?key={Uri.EscapeDataString(_apiKey)}&q={Uri.EscapeDataString(query)}&per_page={count}&image_type=photo";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            return doc.RootElement.GetProperty("hits").EnumerateArray()
                .Select(hit =>
                {
                    var id = hit.GetProperty("id").GetInt64().ToString();
                    var tags = hit.TryGetProperty("tags", out var t) ? t.GetString() ?? query : query;
                    var url = GetBestSourceUrl(hit);
                    var thumb = hit.TryGetProperty("previewURL", out var p) ? p.GetString() ?? string.Empty : url;
                    return new ImageCandidate($"pixabay:{id}", tags, url, thumb, "pixabay");
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pixabay search failed for query '{Query}'", query);
            return [];
        }
    }

    private static string GetBestSourceUrl(JsonElement hit)
    {
        // "largeImageURL" = ~1280px. Skipping "fullHDURL" (premium-only, usually absent) and "imageURL" (full 4000px original).
        foreach (var propertyName in new[] { "largeImageURL", "webformatURL", "imageURL" })
        {
            if (hit.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return string.Empty;
    }
}
