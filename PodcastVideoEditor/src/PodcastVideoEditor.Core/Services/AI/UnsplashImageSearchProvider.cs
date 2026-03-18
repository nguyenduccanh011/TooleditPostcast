using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Fetches image candidates from Unsplash API.
/// Rate limit: 50 req/hr (demo key).
/// </summary>
public sealed class UnsplashImageSearchProvider : IImageSearchProvider
{
    public string ProviderName => "unsplash";

    // Shared static HttpClient for Unsplash.
    private static readonly HttpClient _sharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly string _accessKey;

    public UnsplashImageSearchProvider(ImageSearchSettings settings, HttpClient? httpClient = null)
    {
        _http = httpClient ?? _sharedHttp;
        _accessKey = settings.UnsplashApiKey;
    }

    public async Task<ImageCandidate[]> SearchAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_accessKey)) return [];

        try
        {
            var url = $"https://api.unsplash.com/search/photos?query={Uri.EscapeDataString(query)}&per_page={count}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", _accessKey);

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            return doc.RootElement.GetProperty("results").EnumerateArray()
                .Select(photo =>
                {
                    var id = photo.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
                    var desc = photo.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;
                    desc ??= photo.TryGetProperty("alt_description", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString() ?? query : query;
                    var urls = photo.GetProperty("urls");
                    var url = GetBestSourceUrl(urls);
                    var thumb = urls.TryGetProperty("small", out var s) ? s.GetString() ?? string.Empty : url;
                    return new ImageCandidate($"unsplash:{id}", desc, url, thumb, "unsplash");
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unsplash search failed for query '{Query}'", query);
            return [];
        }
    }

    private static string GetBestSourceUrl(JsonElement urls)
    {
        // "regular" = ~1080px wide — sufficient for 1080p render; avoids downloading raw/full (4000-6000px).
        foreach (var propertyName in new[] { "regular", "full", "raw" })
        {
            if (urls.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return string.Empty;
    }
}
