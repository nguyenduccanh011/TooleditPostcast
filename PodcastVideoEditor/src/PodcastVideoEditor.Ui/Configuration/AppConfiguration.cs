using Microsoft.Extensions.Configuration;

namespace PodcastVideoEditor.Ui.Configuration;

public sealed class AppConfiguration
{
    public AppOptions App { get; set; } = new();

    public UpdateOptions Update { get; set; } = new();

    public AIAnalysisDefaults AIAnalysis { get; set; } = new();

    public ImageSearchDefaults ImageSearch { get; set; } = new();

    public static AppConfiguration Load(string baseDirectory)
    {
        var configurationRoot = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var config = new AppConfiguration();
        configurationRoot.GetSection("App").Bind(config.App);
        configurationRoot.GetSection("Update").Bind(config.Update);
        configurationRoot.GetSection("AIAnalysis").Bind(config.AIAnalysis);
        configurationRoot.GetSection("ImageSearch").Bind(config.ImageSearch);
        return config;
    }
}

public sealed class AIAnalysisDefaults
{
    public string YesScaleApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Pre-configured API key profiles for release builds.</summary>
    public List<BundledProfileEntry> ApiKeyProfiles { get; set; } = new();

    /// <summary>Pre-configured fallback entries for release builds.</summary>
    public List<BundledFallbackConfig> FallbackEntries { get; set; } = new();
}

/// <summary>Bundled profile: Name + ApiKey (bound from appsettings.json).</summary>
public sealed class BundledProfileEntry
{
    public string Name   { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Bundled fallback: ModelId + ProfileName (bound from appsettings.json).</summary>
public sealed class BundledFallbackConfig
{
    public string ModelId     { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
}

public sealed class ImageSearchDefaults
{
    public string PexelsApiKey { get; set; } = string.Empty;
    public string PixabayApiKey { get; set; } = string.Empty;
    public string UnsplashApiKey { get; set; } = string.Empty;
}

public sealed class AppOptions
{
    public string Name { get; set; } = "Podcast Video Editor";

    public string AppDataPath { get; set; } = @"%APPDATA%\PodcastVideoEditor";
}

public sealed class UpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = "GitHubReleases";

    public string Repository { get; set; } = "nguyenduccanh011/TooleditPostcast";

    public string Channel { get; set; } = "stable";

    public bool CheckOnStartup { get; set; } = true;

    public int CheckIntervalHours { get; set; } = 24;
}
