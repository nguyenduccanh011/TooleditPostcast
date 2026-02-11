#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Parses script text in format [start_sec → end_sec] content.
/// ST-3b: One line = one segment (Start, End, Text).
/// </summary>
public static class ScriptParser
{
    /// <summary>
    /// Regex: [0.00 → 6.04]  optional spaces, then rest of line as content.
    /// Captures: (1) start number, (2) end number, (3) content (trimmed).
    /// </summary>
    private static readonly Regex LineRegex = new(
        @"\[\s*(\d+\.?\d*)\s*→\s*(\d+\.?\d*)\]\s*(.*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parsed segment: Start (seconds), End (seconds), Text.
    /// </summary>
    public record ParsedSegment(double Start, double End, string Text);

    /// <summary>
    /// Parse script string into list of segments. Invalid lines are skipped.
    /// </summary>
    /// <param name="scriptText">Raw pasted script (multiline).</param>
    /// <returns>List of parsed segments, ordered by Start time. Empty if no valid lines.</returns>
    public static List<ParsedSegment> Parse(string? scriptText)
    {
        var result = new List<ParsedSegment>();
        if (string.IsNullOrWhiteSpace(scriptText))
            return result;

        var lines = scriptText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var segment = TryParseLine(line.Trim());
            if (segment != null)
                result.Add(segment);
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>
    /// Try parse a single line. Returns null if line does not match format.
    /// </summary>
    public static ParsedSegment? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = LineRegex.Match(line);
        if (!match.Success)
            return null;

        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var start))
            return null;
        if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var end))
            return null;

        var text = match.Groups[3].Value.Trim();
        if (end < start)
            end = start;

        return new ParsedSegment(start, end, text);
    }
}
