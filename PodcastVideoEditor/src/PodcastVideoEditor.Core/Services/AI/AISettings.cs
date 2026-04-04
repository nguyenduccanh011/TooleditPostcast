namespace PodcastVideoEditor.Core.Services.AI;

// ── API Key Profile ──────────────────────────────────────────────────────────

/// <summary>
/// A named API key that gives access to a group of models.
/// All profiles share the same base URL.
/// </summary>
public record ApiKeyProfile
{
    public string Id      { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name    { get; init; } = string.Empty;
    public string ApiKey  { get; init; } = string.Empty;
    public bool   Enabled { get; init; } = true;

    /// <summary>Shows only the last 4 characters, e.g. "sk-…Ab12".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string MaskedKey => ApiKey.Length > 4
        ? $"…{ApiKey[^4..]}"
        : new string('*', ApiKey.Length);
}

/// <summary>
/// A model + the profile whose API key should be used to call it.
/// Used in the ordered fallback list.
/// </summary>
public record ModelFallbackEntry
{
    public string ModelId   { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;

    public override string ToString() => ModelId;
}

// ── Runtime settings interface ───────────────────────────────────────────────

public interface IRuntimeApiSettings
{
    string YesScaleApiKey { get; }
    string YesScaleBaseUrl { get; }
    string YesScaleModel { get; }
    /// <summary>
    /// Ordered list of fallback model+key entries. When the primary model fails with
    /// a transient error, the provider tries these in order, each with its own API key.
    /// </summary>
    IReadOnlyList<ModelFallbackEntry> YesScaleFallbackEntries { get; }
    /// <summary>All configured API key profiles.</summary>
    IReadOnlyList<ApiKeyProfile> ApiKeyProfiles { get; }
    /// <summary>Profile ID for the primary model.</summary>
    string PrimaryProfileId { get; }
    string PexelsApiKey { get; }
    string PixabayApiKey { get; }
    string UnsplashApiKey { get; }

    /// <summary>Resolve the API key for a given profile ID. Returns primary key if not found.</summary>
    string ResolveApiKey(string profileId);
}

internal sealed class LegacyRuntimeApiSettings : IRuntimeApiSettings
{
    public LegacyRuntimeApiSettings(AIAnalysisSettings? ai = null, ImageSearchSettings? image = null)
    {
        YesScaleApiKey = ai?.YesScaleApiKey ?? string.Empty;
        YesScaleBaseUrl = ai?.BaseUrl ?? "https://api.yescale.vip/v1";
        YesScaleModel = ai?.DefaultModel ?? "gpt-4o-mini";
        YesScaleFallbackEntries = Array.Empty<ModelFallbackEntry>();
        ApiKeyProfiles = Array.Empty<ApiKeyProfile>();
        PrimaryProfileId = string.Empty;
        PexelsApiKey = image?.PexelsApiKey ?? string.Empty;
        PixabayApiKey = image?.PixabayApiKey ?? string.Empty;
        UnsplashApiKey = image?.UnsplashApiKey ?? string.Empty;
    }

    public string YesScaleApiKey { get; }
    public string YesScaleBaseUrl { get; }
    public string YesScaleModel { get; }
    public IReadOnlyList<ModelFallbackEntry> YesScaleFallbackEntries { get; }
    public IReadOnlyList<ApiKeyProfile> ApiKeyProfiles { get; }
    public string PrimaryProfileId { get; }
    public string PexelsApiKey { get; }
    public string PixabayApiKey { get; }
    public string UnsplashApiKey { get; }
    public string ResolveApiKey(string profileId) => YesScaleApiKey;
}

/// <summary>
/// Configuration for the YesScale AI analysis service.
/// Bound from appsettings.json "AIAnalysis" section.
/// </summary>
public record AIAnalysisSettings
{
    public string YesScaleApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.yescale.vip/v1";
    public string DefaultModel { get; init; } = "gemini-2.5-flash-lite-nothinking";
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
