using System;

namespace PodcastVideoEditor.Ui.Services.Update;

public sealed class UpdateCheckResult
{
    public bool IsSuccessful { get; init; }

    public bool IsUpdateAvailable { get; init; }

    public bool WasSkipped { get; init; }

    public string Message { get; init; } = string.Empty;

    public Version? CurrentVersion { get; init; }

    public Version? LatestVersion { get; init; }

    public string? ReleaseTitle { get; init; }

    public string? DownloadUrl { get; init; }

    public string? ReleasePageUrl { get; init; }

    public static UpdateCheckResult Skipped(string message) => new()
    {
        IsSuccessful = false,
        WasSkipped = true,
        Message = message
    };

    public static UpdateCheckResult Failure(string message) => new()
    {
        IsSuccessful = false,
        Message = message
    };
}
