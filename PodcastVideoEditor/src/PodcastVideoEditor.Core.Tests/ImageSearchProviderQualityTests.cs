using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PodcastVideoEditor.Core.Services.AI;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class ImageSearchProviderQualityTests
{
    [Fact]
    public async Task PexelsProvider_PrefersLarge2xOverOriginalImageUrl()
    {
        // large2x (~1880px) preferred over original (4000px+) to avoid unnecessary bandwidth/storage.
        var json = """
        {
          "photos": [
            {
              "id": 42,
              "alt": "studio portrait",
              "src": {
                "small": "https://img.example/pexels-small.jpg",
                "medium": "https://img.example/pexels-medium.jpg",
                "large2x": "https://img.example/pexels-large2x.jpg",
                "original": "https://img.example/pexels-original.jpg"
              }
            }
          ]
        }
        """;

        var provider = new PexelsImageSearchProvider(
            new ImageSearchSettings { PexelsApiKey = "token" },
            CreateHttpClient(json));

        var results = await provider.SearchAsync("portrait");

        var candidate = Assert.Single(results);
        Assert.Equal("https://img.example/pexels-large2x.jpg", candidate.Url);
        Assert.Equal("https://img.example/pexels-small.jpg", candidate.ThumbnailUrl);
    }

    [Fact]
    public async Task PixabayProvider_PrefersLargeImageUrl()
    {
        var json = """
        {
          "hits": [
            {
              "id": 99,
              "tags": "podcast host",
              "previewURL": "https://img.example/pixabay-preview.jpg",
              "webformatURL": "https://img.example/pixabay-web.jpg",
              "largeImageURL": "https://img.example/pixabay-large.jpg"
            }
          ]
        }
        """;

        var provider = new PixabayImageSearchProvider(
            new ImageSearchSettings { PixabayApiKey = "token" },
            CreateHttpClient(json));

        var results = await provider.SearchAsync("podcast");

        var candidate = Assert.Single(results);
        Assert.Equal("https://img.example/pixabay-large.jpg", candidate.Url);
        Assert.Equal("https://img.example/pixabay-preview.jpg", candidate.ThumbnailUrl);
    }

    [Fact]
    public async Task UnsplashProvider_PrefersRegularOverFullImageUrl()
    {
        // "regular" (~1080px wide) preferred over "full" (4000-6000px) to avoid unnecessary bandwidth/storage.
        var json = """
        {
          "results": [
            {
              "id": "abc123",
              "alt_description": "podcast setup",
              "urls": {
                "small": "https://img.example/unsplash-small.jpg",
                "regular": "https://img.example/unsplash-regular.jpg",
                "full": "https://img.example/unsplash-full.jpg"
              }
            }
          ]
        }
        """;

        var provider = new UnsplashImageSearchProvider(
            new ImageSearchSettings { UnsplashApiKey = "token" },
            CreateHttpClient(json));

        var results = await provider.SearchAsync("podcast");

        var candidate = Assert.Single(results);
        Assert.Equal("https://img.example/unsplash-regular.jpg", candidate.Url);
        Assert.Equal("https://img.example/unsplash-small.jpg", candidate.ThumbnailUrl);
    }

    private static HttpClient CreateHttpClient(string content)
        => new(new StubHttpMessageHandler(content))
        {
            BaseAddress = new System.Uri("https://example.test")
        };

    private sealed class StubHttpMessageHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
    }
}