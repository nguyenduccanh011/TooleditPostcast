using System;
using System.IO;
using System.Reflection;

namespace PodcastVideoEditor.Ui.Services;

public interface IAppInfoService
{
    string ProductName { get; }

    Version CurrentVersion { get; }

    string DisplayVersion { get; }

    string InstallDirectory { get; }

    string? BundledFfmpegPath { get; }

    string? BundledFfprobePath { get; }
}

public sealed class AppInfoService : IAppInfoService
{
    private readonly Assembly _entryAssembly;

    public AppInfoService()
    {
        _entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        ProductName = "Podcast Video Editor";
        CurrentVersion = ResolveVersion(_entryAssembly);
        DisplayVersion = VersionParser.ToDisplayString(CurrentVersion);
        InstallDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        BundledFfmpegPath = ResolveBundledToolPath("ffmpeg.exe");
        BundledFfprobePath = ResolveBundledToolPath("ffprobe.exe");
    }

    public string ProductName { get; }

    public Version CurrentVersion { get; }

    public string DisplayVersion { get; }

    public string InstallDirectory { get; }

    public string? BundledFfmpegPath { get; }

    public string? BundledFfprobePath { get; }

    private static Version ResolveVersion(Assembly assembly)
    {
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (VersionParser.TryParseReleaseVersion(informationalVersion, out var parsedVersion))
            return parsedVersion;

        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    private static string? ResolveBundledToolPath(string fileName)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", fileName);
        return File.Exists(bundledPath) ? bundledPath : null;
    }
}
