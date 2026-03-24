namespace PodcastVideoEditor.Core.Services.AI;

public interface IRuntimeApiSettings
{
    string YesScaleApiKey { get; }
    string YesScaleBaseUrl { get; }
    string YesScaleModel { get; }
    string PexelsApiKey { get; }
    string PixabayApiKey { get; }
    string UnsplashApiKey { get; }
}

internal sealed class LegacyRuntimeApiSettings : IRuntimeApiSettings
{
    public LegacyRuntimeApiSettings(AIAnalysisSettings? ai = null, ImageSearchSettings? image = null)
    {
        YesScaleApiKey = ai?.YesScaleApiKey ?? string.Empty;
        YesScaleBaseUrl = ai?.BaseUrl ?? "https://api.yescale.vip/v1";
        YesScaleModel = ai?.DefaultModel ?? "gpt-4o-mini";
        PexelsApiKey = image?.PexelsApiKey ?? string.Empty;
        PixabayApiKey = image?.PixabayApiKey ?? string.Empty;
        UnsplashApiKey = image?.UnsplashApiKey ?? string.Empty;
    }

    public string YesScaleApiKey { get; }
    public string YesScaleBaseUrl { get; }
    public string YesScaleModel { get; }
    public string PexelsApiKey { get; }
    public string PixabayApiKey { get; }
    public string UnsplashApiKey { get; }
}

/// <summary>
/// Configuration for the YesScale AI analysis service.
/// Bound from appsettings.json "AIAnalysis" section.
/// </summary>
public record AIAnalysisSettings
{
    public string YesScaleApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.yescale.vip/v1";
    public string DefaultModel { get; init; } = "gpt-4o-mini";
}

/// <summary>
/// Configuration for the image search providers.
/// Bound from appsettings.json "ImageSearch" section.
/// </summary>
public record ImageSearchSettings
{
    public string PexelsApiKey { get; init; } = string.Empty;
    public string PixabayApiKey { get; init; } = string.Empty;
    public string UnsplashApiKey { get; init; } = string.Empty;
}
