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
        Assert.Contains("x='max(0,(iw-iw/zoom))*(0.0200+(1-2*0.0200)*(1-(0.5-0.5*cos(PI*on/150.0))))'", filter);
        Assert.Contains("force_original_aspect_ratio=increase,crop=1080:1920", filter);
        Assert.Contains(":s=1080x1920:fps=30", filter);
        Assert.Contains(",format=yuv420p", filter);
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
        Assert.Contains("x='max(0,(iw-iw/zoom))*(0.0200+(1-2*0.0200)*(0.5-0.5*cos(PI*on/150.0)))'", filter);
        Assert.Contains("force_original_aspect_ratio=increase,crop=1080:1920", filter);
        Assert.Contains(":s=1080x1920:fps=30", filter);
        Assert.Contains(",format=yuv420p", filter);
        Assert.DoesNotContain("x='iw*", filter);
    }

    [Fact]
    public void BuildZoompanFilter_ShortSegment_DampsZoomVelocityToAvoidPulse()
    {
        var seg = new RenderVisualSegment
        {
            IsVideo = false,
            MotionPreset = MotionPresets.ZoomIn,
            MotionIntensity = 1.0,
            StartTime = 0,
            EndTime = 1
        };

        var filter = MotionFilterBuilder.BuildZoompanFilter(seg, fps: 30, renderWidth: 1080, renderHeight: 1920);

        Assert.NotNull(filter);
        Assert.Contains("z='1.0+0.005867*on'", filter);
        Assert.DoesNotContain("z='1.0+0.013333*on'", filter);
    }

    [Fact]
    public void BuildZoompanFilter_LongSegment_UsesHigherSupersampleToReduceQuantization()
    {
        var seg = new RenderVisualSegment
        {
            IsVideo = false,
            MotionPreset = MotionPresets.ZoomIn,
            MotionIntensity = 1.0,
            StartTime = 0,
            EndTime = 12.5
        };

        var filter = MotionFilterBuilder.BuildZoompanFilter(seg, fps: 30, renderWidth: 1080, renderHeight: 1920);

        Assert.NotNull(filter);
        Assert.Contains("scale=3240:5760:flags=bilinear", filter);
        Assert.Contains(":s=1080x1920:fps=30", filter);
    }
}
