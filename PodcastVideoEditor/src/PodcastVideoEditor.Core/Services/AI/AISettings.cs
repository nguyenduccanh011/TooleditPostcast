namespace PodcastVideoEditor.Core.Services.AI;

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
