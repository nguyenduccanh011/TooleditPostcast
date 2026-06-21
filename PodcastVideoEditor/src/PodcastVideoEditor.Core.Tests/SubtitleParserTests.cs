using PodcastVideoEditor.Core.Utilities;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class SubtitleParserTests
{
    [Fact]
    public void Parse_Srt_BasicCues()
    {
        const string srt =
            "1\n" +
            "00:00:01,000 --> 00:00:04,000\n" +
            "Hello world\n" +
            "\n" +
            "2\n" +
            "00:00:05,500 --> 00:00:08,000\n" +
            "Second line\n";

        var segs = SubtitleParser.Parse(srt);

        Assert.Equal(2, segs.Count);
        Assert.Equal(1.0, segs[0].Start, 3);
        Assert.Equal(4.0, segs[0].End, 3);
        Assert.Equal("Hello world", segs[0].Text);
        Assert.Equal(5.5, segs[1].Start, 3);
        Assert.Equal(8.0, segs[1].End, 3);
        Assert.Equal("Second line", segs[1].Text);
    }

    [Fact]
    public void Parse_Srt_MultiLineTextJoined()
    {
        const string srt =
            "1\n" +
            "00:00:00,000 --> 00:00:03,000\n" +
            "Line one\n" +
            "Line two\n";

        var segs = SubtitleParser.Parse(srt);

        Assert.Single(segs);
        Assert.Equal("Line one Line two", segs[0].Text);
    }

    [Fact]
    public void Parse_Vtt_WithHeaderAndTags()
    {
        const string vtt =
            "WEBVTT\n" +
            "\n" +
            "00:00:01.000 --> 00:00:03.000 align:start\n" +
            "<c.yellow>Styled</c> text\n";

        var segs = SubtitleParser.Parse(vtt);

        Assert.Single(segs);
        Assert.Equal(1.0, segs[0].Start, 3);
        Assert.Equal(3.0, segs[0].End, 3);
        Assert.Equal("Styled text", segs[0].Text);
    }

    [Fact]
    public void Parse_Vtt_MinutesOnlyTimestamp()
    {
        // VTT allows MM:SS.mmm (no hours).
        const string vtt =
            "WEBVTT\n\n" +
            "01:30.000 --> 01:32.500\n" +
            "Short form\n";

        var segs = SubtitleParser.Parse(vtt);

        Assert.Single(segs);
        Assert.Equal(90.0, segs[0].Start, 3);
        Assert.Equal(92.5, segs[0].End, 3);
    }

    [Fact]
    public void Parse_SkipsZeroAndNegativeDurationCues()
    {
        const string srt =
            "1\n00:00:02,000 --> 00:00:02,000\nzero\n\n" +   // zero duration → skipped
            "2\n00:00:05,000 --> 00:00:03,000\ninverted\n\n" + // inverted → skipped
            "3\n00:00:06,000 --> 00:00:09,000\nvalid\n";

        var segs = SubtitleParser.Parse(srt);

        Assert.Single(segs);
        Assert.Equal("valid", segs[0].Text);
    }

    [Fact]
    public void Parse_SortsByStartTime()
    {
        const string srt =
            "1\n00:00:10,000 --> 00:00:12,000\nlater\n\n" +
            "2\n00:00:02,000 --> 00:00:04,000\nearlier\n";

        var segs = SubtitleParser.Parse(srt);

        Assert.Equal(2, segs.Count);
        Assert.Equal("earlier", segs[0].Text);
        Assert.Equal("later", segs[1].Text);
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(SubtitleParser.Parse(null));
        Assert.Empty(SubtitleParser.Parse("   "));
        Assert.Empty(SubtitleParser.Parse("no timestamps here"));
    }
}
