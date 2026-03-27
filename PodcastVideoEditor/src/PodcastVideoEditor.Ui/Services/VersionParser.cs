using System;
using System.Text.RegularExpressions;

namespace PodcastVideoEditor.Ui.Services;

internal static partial class VersionParser
{
    public static bool TryParseReleaseVersion(string? rawValue, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        var normalized = rawValue.Trim();
        if (normalized.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["refs/tags/".Length..];

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var match = VersionPrefixRegex().Match(normalized);
        if (!match.Success)
            return false;

        if (!Version.TryParse(match.Value, out var parsedVersion))
            return false;

        version = parsedVersion;
        return true;
    }

    public static string ToDisplayString(Version version)
    {
        if (version.Build >= 0)
            return $"{version.Major}.{version.Minor}.{version.Build}";

        return $"{version.Major}.{version.Minor}";
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}")]
    private static partial Regex VersionPrefixRegex();
}
