#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Parses SubRip (.srt) and WebVTT (.vtt) subtitle files into the same
/// <see cref="ScriptParser.ParsedSegment"/> shape used by the script pipeline,
/// so imported subtitles flow through the existing "apply to text track" logic.
/// </summary>
public static class SubtitleParser
{
    // A cue timing line: "00:00:01,000 --> 00:00:04,000" (SRT, comma) or
    // "00:00:01.000 --> 00:00:04.000" / "00:01.000 --> 00:04.000" (VTT, dot).
    // Hours are optional; trailing VTT cue settings after the end stamp are ignored.
    private static readonly Regex TimeLineRegex = new(
        @"(\d{1,2}:)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d{1,2}:)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})",
        RegexOptions.Compiled);

    // Inline WebVTT tags to strip from cue text: <c>, </c>, <v Bob>, <00:00:01.000>, etc.
    private static readonly Regex VttTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Parse SRT or VTT content (format auto-detected per cue). Invalid or
    /// zero/negative-duration cues are skipped. Result is sorted by start time.
    /// </summary>
    public static List<ScriptParser.ParsedSegment> Parse(string? content)
    {
        var result = new List<ScriptParser.ParsedSegment>();
        if (string.IsNullOrWhiteSpace(content))
            return result;

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        double? curStart = null, curEnd = null;
        var textLines = new List<string>();

        void Flush()
        {
            if (curStart.HasValue && curEnd.HasValue && curEnd > curStart)
            {
                var text = string.Join(" ", textLines).Trim();
                result.Add(new ScriptParser.ParsedSegment(curStart.Value, curEnd.Value, text));
            }
            curStart = curEnd = null;
            textLines.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var m = TimeLineRegex.Match(line);
            if (m.Success)
            {
                // A new timing line begins a new cue — commit the previous one first.
                Flush();
                curStart = ToSeconds(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
                curEnd = ToSeconds(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
                continue;
            }

            if (curStart.HasValue)
            {
                // Inside a cue: a blank line ends it; any other line is cue text.
                if (line.Length == 0) { Flush(); continue; }
                textLines.Add(VttTagRegex.Replace(line, string.Empty));
            }
            // Lines before the first cue (WEBVTT header, NOTE blocks, SRT indices) are ignored.
        }
        Flush();

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static double ToSeconds(string hh, string mm, string ss, string ms)
    {
        int h = 0;
        if (!string.IsNullOrEmpty(hh))
            int.TryParse(hh.TrimEnd(':'), out h);
        int.TryParse(mm, out var m);
        int.TryParse(ss, out var s);
        // Milliseconds may be 1-3 digits ("5" => 500ms, "05" => 50ms, "050" => 50ms).
        var msPadded = (ms + "000").Substring(0, 3);
        int.TryParse(msPadded, out var milli);
        return h * 3600 + m * 60 + s + milli / 1000.0;
    }
}
