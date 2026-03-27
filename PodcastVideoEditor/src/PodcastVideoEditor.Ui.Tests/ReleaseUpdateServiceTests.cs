using PodcastVideoEditor.Ui.Configuration;
using PodcastVideoEditor.Ui.Services;
using PodcastVideoEditor.Ui.Services.Update;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Fact]
    public void VersionParser_StripsTagPrefix_AndPrereleaseSuffix()
    {
        var parsed = VersionParser.TryParseReleaseVersion("v1.2.3-beta.1", out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(1, 2, 3), version);
    }

    [Fact]
    public void SelectInstallerAsset_PrefersSetupExe()
    {
        var asset = GitHubReleaseUpdateService.SelectInstallerAsset(
        [
            new GitHubReleaseUpdateService.GitHubReleaseAsset { Name = "PodcastVideoEditor-win-x64-v1.0.1.zip", BrowserDownloadUrl = "https://example.com/portable.zip" },
            new GitHubReleaseUpdateService.GitHubReleaseAsset { Name = "PodcastVideoEditor-Setup-v1.0.1.exe", BrowserDownloadUrl = "https://example.com/setup.exe" },
            new GitHubReleaseUpdateService.GitHubReleaseAsset { Name = "PodcastVideoEditor-Installer-v1.0.1.exe", BrowserDownloadUrl = "https://example.com/installer.exe" }
        ]);

        Assert.NotNull(asset);
        Assert.Equal("PodcastVideoEditor-Setup-v1.0.1.exe", asset!.Name);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpdate_WhenGitHubReleaseIsNewer()
    {
        using var tempDir = new TemporaryDirectory();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "tag_name": "v1.1.0",
                  "name": "Podcast Video Editor v1.1.0",
                  "html_url": "https://github.com/nguyenduccanh011/TooleditPostcast/releases/tag/v1.1.0",
                  "assets": [
                    {
                      "name": "PodcastVideoEditor-Setup-v1.1.0.exe",
                      "browser_download_url": "https://github.com/nguyenduccanh011/TooleditPostcast/releases/download/v1.1.0/PodcastVideoEditor-Setup-v1.1.0.exe"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        var service = new GitHubReleaseUpdateService(
            CreateConfiguration(),
            new FakeAppInfoService(new Version(1, 0, 0)),
            new UserSettingsStore(),
            tempDir.Path,
            new HttpClient(handler));

        var result = await service.CheckForUpdatesAsync(ignoreSchedule: true);

        Assert.True(result.IsSuccessful);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 1, 0), result.LatestVersion);
        Assert.Equal("https://github.com/nguyenduccanh011/TooleditPostcast/releases/download/v1.1.0/PodcastVideoEditor-Setup-v1.1.0.exe", result.DownloadUrl);
    }

    private static AppConfiguration CreateConfiguration()
    {
        return new AppConfiguration
        {
            Update = new UpdateOptions
            {
                Enabled = true,
                Provider = "GitHubReleases",
                Repository = "nguyenduccanh011/TooleditPostcast",
                Channel = "stable",
                CheckOnStartup = true,
                CheckIntervalHours = 24
            }
        };
    }

    private sealed class FakeAppInfoService : IAppInfoService
    {
        public FakeAppInfoService(Version version)
        {
            CurrentVersion = version;
            DisplayVersion = VersionParser.ToDisplayString(version);
        }

        public string ProductName => "Podcast Video Editor";

        public Version CurrentVersion { get; }

        public string DisplayVersion { get; }

        public string InstallDirectory => "C:\\Program Files\\PodcastVideoEditor";

        public string? BundledFfmpegPath => null;

        public string? BundledFfprobePath => null;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PodcastVideoEditor.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
