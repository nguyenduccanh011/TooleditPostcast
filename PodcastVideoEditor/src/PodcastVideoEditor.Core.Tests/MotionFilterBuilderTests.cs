using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class MotionFilterBuilderTests
{
    [Fact]
    public void BuildZoompanFilter_PanLeft_UsesZoomAwareTravelDistance()
    {
        var seg = new RenderVisualSegment
        {
            IsVideo = false,
            MotionPreset = MotionPresets.PanLeft,
            MotionIntensity = 1.0,
            StartTime = 0,
            EndTime = 5
        };

        var filter = MotionFilterBuilder.BuildZoompanFilter(seg, fps: 30, renderWidth: 1080, renderHeight: 1920);

        Assert.NotNull(filter);
        Assert.Contains("x='min(max(0,(iw-iw/zoom)),max(60.0000,max(0,(iw-iw/zoom))*0.3000))*(1-(0.5-0.5*cos(PI*on/150.0)))'", filter);
        Assert.Contains(":s=2160x3840:fps=30", filter);
        Assert.Contains("scale=1080:1920:flags=lanczos+accurate_rnd+full_chroma_int", filter);
        Assert.DoesNotContain("x='iw*", filter);
    }

    [Fact]
    public void BuildZoompanFilter_ZoomInPanRight_UsesZoomAwareTravelDistance()
    {
        var seg = new RenderVisualSegment
        {
            IsVideo = false,
            MotionPreset = MotionPresets.ZoomInPanRight,
            MotionIntensity = 1.0,
            StartTime = 0,
            EndTime = 5
        };

        var filter = MotionFilterBuilder.BuildZoompanFilter(seg, fps: 30, renderWidth: 1080, renderHeight: 1920);

        Assert.NotNull(filter);
        Assert.Contains("z='1.0+0.002667*on'", filter);
        Assert.Contains("x='min(max(0,(iw-iw/zoom)),max(60.0000,max(0,(iw-iw/zoom))*0.3000))*(0.5-0.5*cos(PI*on/150.0))'", filter);
        Assert.Contains(":s=2160x3840:fps=30", filter);
        Assert.Contains("scale=1080:1920:flags=lanczos+accurate_rnd+full_chroma_int", filter);
        Assert.DoesNotContain("x='iw*", filter);
    }
}
