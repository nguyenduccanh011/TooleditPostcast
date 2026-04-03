using System.Reflection;
using PodcastVideoEditor.Core.Services.AI;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class YesScaleProviderParsingTests
{
    [Fact]
    public void ParseAnalysisResponse_HandlesLeadingTextAndJsonCodeFence()
    {
        var raw = """
[0.0 - 6.04] hãy trả lời ngắn gọn cho tôi biết, ở tab script sau khi ấn phân tích ai sẽ thực hiện gì

```json
[
  {
    "startTime": "0.0",
    "endTime": "6.04",
    "text": "Please briefly tell me, in the script after clicking analyze, who will do what?",
    "keywords": ["script analysis", "user action", "data processing", "software function", "task execution"]
  }
]
```
""";

        var result = InvokeParseAnalysisResponse(raw);

        Assert.Single(result);
        Assert.Equal(0.0, result[0].StartTime);
        Assert.Equal(6.04, result[0].EndTime);
        Assert.Equal(3, result[0].Keywords.Length);
    }

    [Fact]
    public void ParseAnalysisResponse_HandlesInlineJsonWithoutFence()
    {
        var raw = "Trả lời như sau: [{\"startTime\":0.0,\"endTime\":1.25,\"text\":\"abc\",\"keywords\":[\"one\",\"two\",\"three\",\"four\",\"five\"]}]";

        var result = InvokeParseAnalysisResponse(raw);

        Assert.Single(result);
        Assert.Equal(1.25, result[0].EndTime);
        Assert.Equal("abc", result[0].Text);
    }

    private static AISegment[] InvokeParseAnalysisResponse(string raw)
    {
        var method = typeof(YesScaleProvider).GetMethod(
            "ParseAnalysisResponse",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [raw]);
        return Assert.IsType<AISegment[]>(result);
    }
}