using Microsoft.Extensions.Configuration;

namespace PodcastVideoEditor.Ui.Configuration;

public sealed class AppConfiguration
{
    public AppOptions App { get; set; } = new();

    public UpdateOptions Update { get; set; } = new();

    public static AppConfiguration Load(string baseDirectory)
    {
        var configurationRoot = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var config = new AppConfiguration();
        configurationRoot.GetSection("App").Bind(config.App);
        configurationRoot.GetSection("Update").Bind(config.Update);
        return config;
    }
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
