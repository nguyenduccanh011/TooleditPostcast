using System.Collections.Generic;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using SkiaSharp;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class CompositorSceneTests
{
    private static RenderVisualSegment Image(double start, double end, int z = 0, string? motion = null)
        => new()
        {
            SourcePath = $"img_{start}_{end}.png",
            StartTime = start,
            EndTime = end,
            IsVideo = false,
            ZOrder = z,
            MotionPreset = motion ?? MotionPresets.None,
            MotionIntensity = 1.0,
        };

    // ── Scene builder: activation by time ──────────────────────────────────

    [Fact]
    public void BuildAt_OnlyIncludesSegmentsActiveAtTime()
    {
        var segs = new List<RenderVisualSegment> { Image(2, 5) };

        Assert.Empty(CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 1.0).Layers);   // before
        Assert.Single(CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 3.0).Layers);  // inside
        Assert.Empty(CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 5.0).Layers);   // end is exclusive
        Assert.Empty(CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 6.0).Layers);   // after
    }

    [Fact]
    public void BuildAt_OrdersLayersByZOrder()
    {
        var segs = new List<RenderVisualSegment>
        {
            Image(0, 10, z: 5),
            Image(0, 10, z: 1),
            Image(0, 10, z: 3),
        };

        var scene = CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 1.0);

        Assert.Equal(3, scene.Layers.Count);
        Assert.Equal(1, scene.Layers[0].ZOrder);
        Assert.Equal(3, scene.Layers[1].ZOrder);
        Assert.Equal(5, scene.Layers[2].ZOrder);
    }

    // ── Fade ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildAt_AppliesFadeInAndOut()
    {
        var seg = Image(0, 10);
        seg.TransitionType = "fade";
        seg.TransitionDuration = 2.0;
        var segs = new List<RenderVisualSegment> { seg };

        // 1s into a 2s fade-in → ~0.5 opacity
        var mid = CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 1.0).Layers[0].Opacity;
        Assert.InRange(mid, 0.4, 0.6);

        // Fully visible in the middle
        var full = CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 5.0).Layers[0].Opacity;
        Assert.True(full > 0.99);

        // 1s before end, into a 2s fade-out → ~0.5 opacity
        var fadeOut = CompositorSceneBuilder.BuildAt(segs, 100, 100, 30, 9.0).Layers[0].Opacity;
        Assert.InRange(fadeOut, 0.4, 0.6);
    }

    // ── Ken-Burns ────────────────────────────────────────────────────────

    [Fact]
    public void BuildAt_NoMotion_ProducesIdentityTransform()
    {
        var scene = CompositorSceneBuilder.BuildAt(new List<RenderVisualSegment> { Image(0, 5) }, 100, 100, 30, 2.5);
        var l = scene.Layers[0];
        Assert.Equal(1.0, l.MotionScaleX, 3);
        Assert.Equal(1.0, l.MotionScaleY, 3);
        Assert.Equal(0.0, l.MotionTranslateX, 3);
    }

    [Fact]
    public void BuildAt_ZoomIn_ScalesUpOverTime()
    {
        var segs = new List<RenderVisualSegment> { Image(0, 5, motion: MotionPresets.ZoomIn) };

        var early = CompositorSceneBuilder.BuildAt(segs, 1080, 1920, 30, 0.1).Layers[0].MotionScaleX;
        var late = CompositorSceneBuilder.BuildAt(segs, 1080, 1920, 30, 4.9).Layers[0].MotionScaleX;

        Assert.True(early >= 1.0);
        Assert.True(late > early); // zoom grows over the segment
    }

    // ── Renderer smoke test on a CPU surface (no GPU required) ─────────────

    [Fact]
    public void SkiaSceneRenderer_DrawsOpaqueFullFrameLayer()
    {
        var seg = new RenderVisualSegment
        {
            SourcePath = "solid",
            StartTime = 0,
            EndTime = 5,
            IsVideo = false,
            ScaleMode = "Stretch",
        };
        var scene = CompositorSceneBuilder.BuildAt(new List<RenderVisualSegment> { seg }, 100, 100, 30, 1.0);

        using var source = new SolidTextureSource(SKColors.Red);
        var renderer = new SkiaSceneRenderer(source);

        using var surface = SKSurface.Create(new SKImageInfo(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        renderer.Render(surface.Canvas, scene);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(snapshot);
        var center = bmp.GetPixel(50, 50);

        Assert.True(center.Red > 200, $"expected red center, got {center}");
        Assert.True(center.Green < 60);
        Assert.True(center.Blue < 60);
    }

    [Fact]
    public void SkiaSceneRenderer_EmptyScene_ClearsToBlack()
    {
        var scene = CompositorSceneBuilder.BuildAt(new List<RenderVisualSegment>(), 50, 50, 30, 0.0);
        using var source = new SolidTextureSource(SKColors.Red);
        var renderer = new SkiaSceneRenderer(source);

        using var surface = SKSurface.Create(new SKImageInfo(50, 50, SKColorType.Rgba8888, SKAlphaType.Premul));
        renderer.Render(surface.Canvas, scene);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(snapshot);
        var center = bmp.GetPixel(25, 25);
        Assert.True(center.Red < 10 && center.Green < 10 && center.Blue < 10, $"expected black, got {center}");
    }

    private sealed class SolidTextureSource : ICompositorTextureSource, System.IDisposable
    {
        private readonly SKImage _image;
        private readonly SKBitmap _bitmap;

        public SolidTextureSource(SKColor color)
        {
            _bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
            _bitmap.Erase(color);
            _image = SKImage.FromBitmap(_bitmap);
        }

        public SKImage GetImage(CompositorLayer layer, double timeSeconds) => _image;

        public void Dispose()
        {
            _image.Dispose();
            _bitmap.Dispose();
        }
    }
}
