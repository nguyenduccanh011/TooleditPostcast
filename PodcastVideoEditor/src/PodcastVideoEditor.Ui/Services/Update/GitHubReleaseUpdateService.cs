using PodcastVideoEditor.Ui.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.Services.Update;

public sealed class GitHubReleaseUpdateService : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppConfiguration _configuration;
    private readonly IAppInfoService _appInfoService;
    private readonly UserSettingsStore _userSettingsStore;
    private readonly string _appDataPath;
    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService(
        AppConfiguration configuration,
        IAppInfoService appInfoService,
        UserSettingsStore userSettingsStore,
        string appDataPath,
        HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _appInfoService = appInfoService;
        _userSettingsStore = userSettingsStore;
        _appDataPath = appDataPath;
        _httpClient = httpClient ?? new HttpClient();
    }

    public bool ShouldCheckForUpdates()
    {
        if (!IsUpdateChecksEnabled())
            return false;

        if (!_configuration.Update.CheckOnStartup)
            return false;

        if (_userSettingsStore.LastUpdateCheckUtc is null)
            return true;

        var interval = TimeSpan.FromHours(Math.Max(1, _configuration.Update.CheckIntervalHours));
        return DateTime.UtcNow - _userSettingsStore.LastUpdateCheckUtc.Value >= interval;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool ignoreSchedule, CancellationToken cancellationToken = default)
    {
        if (!IsUpdateChecksEnabled())
            return UpdateCheckResult.Skipped("Update checks are disabled in appsettings.json.");

        if (!ignoreSchedule && !ShouldCheckForUpdates())
            return UpdateCheckResult.Skipped("Update check skipped because the configured interval has not elapsed yet.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildLatestReleaseUri());
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PodcastVideoEditor", _appInfoService.DisplayVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            PersistLastCheckedUtc();

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var rateLimitValues) &&
                rateLimitValues.FirstOrDefault() == "0")
            {
                return UpdateCheckResult.Failure("GitHub API rate limit exceeded. Please try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failure(
                    $"Could not check for updates. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(contentStream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (release is null)
                return UpdateCheckResult.Failure("Could not read release metadata from GitHub.");

            if (!VersionParser.TryParseReleaseVersion(release.TagName, out var latestVersion))
            {
                return UpdateCheckResult.Failure(
                    $"Latest release tag '{release.TagName}' is not a supported version tag. Expected format like v1.2.3.");
            }

            var currentVersion = _appInfoService.CurrentVersion;
            if (latestVersion <= currentVersion)
            {
                return new UpdateCheckResult
                {
                    IsSuccessful = true,
                    IsUpdateAvailable = false,
                    Message = $"You are already on the latest version ({VersionParser.ToDisplayString(currentVersion)}).",
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseTitle = release.Name,
                    ReleasePageUrl = release.HtmlUrl
                };
            }

            var selectedAsset = SelectInstallerAsset(release.Assets);
            return new UpdateCheckResult
            {
                IsSuccessful = true,
                IsUpdateAvailable = true,
                Message = $"Version {VersionParser.ToDisplayString(latestVersion)} is available.",
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                ReleaseTitle = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                DownloadUrl = selectedAsset?.BrowserDownloadUrl,
                ReleasePageUrl = release.HtmlUrl
            };
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Skipped("Update check canceled.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            PersistLastCheckedUtc();
            return UpdateCheckResult.Failure($"Could not check for updates: {ex.Message}");
        }
    }

    internal static GitHubReleaseAsset? SelectInstallerAsset(IReadOnlyList<GitHubReleaseAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
            return null;

        return assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .OrderByDescending(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase))
            .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private bool IsUpdateChecksEnabled()
    {
        return _configuration.Update.Enabled &&
               string.Equals(_configuration.Update.Provider, "GitHubReleases", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_configuration.Update.Channel, "stable", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(_configuration.Update.Repository);
    }

    private string BuildLatestReleaseUri()
        => $"https://api.github.com/repos/{_configuration.Update.Repository}/releases/latest";

    private void PersistLastCheckedUtc()
    {
        try
        {
            _userSettingsStore.LastUpdateCheckUtc = DateTime.UtcNow;
            _userSettingsStore.Save(_appDataPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist last update check timestamp");
        }
    }

    internal sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; set; } = new();
    }

    internal sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
