using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Utilities;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class ElementMapperPersistenceTests
{
    [Fact]
    public void VisualizerElement_RoundTrip_PreservesAllTemplateRelevantProperties()
    {
        var original = new VisualizerElement
        {
            Id = "viz-1",
            Name = "My Visualizer",
            X = 12,
            Y = 34,
            Width = 640,
            Height = 220,
            ZIndex = 5,
            Rotation = 7,
            IsVisible = true,
            SegmentId = "segment-1",
            FlipH = true,
            FlipV = true,
            ColorPalette = ColorPalette.Custom,
            BandCount = 48,
            Style = VisualizerStyle.NeonGlow,
            SmoothingFactor = 0.82f,
            ShowPeaks = false,
            SymmetricMode = false,
            PeakHoldTime = 777,
            BarWidth = 13.5f,
            BarSpacing = 4.2f,
            Opacity = 0.63,
            Sensitivity = 2.4f,
            MinFrequency = 120,
            MaxFrequency = 16800,
            BarCornerRadius = 6.5f,
            PrimaryColorHex = "#12ABCD",
            GlowIntensity = 1.7f,
            AnimationSpeed = 2.3f,
            CustomGradientColors = "#111111,#222222,#333333",
            BarGradientDarkness = 0.55f,
            BarGradientEnabled = false,
            BarGradientBaseColorHex = "#010203"
        };

        var entity = ElementMapper.ToElement(original, "project-1");
        var roundTripped = ElementMapper.ToCanvasElement(entity);

        var restored = Assert.IsType<VisualizerElement>(roundTripped);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.ColorPalette, restored.ColorPalette);
        Assert.Equal(original.BandCount, restored.BandCount);
        Assert.Equal(original.Style, restored.Style);
        Assert.Equal(original.ShowPeaks, restored.ShowPeaks);
        Assert.Equal(original.PeakHoldTime, restored.PeakHoldTime);
        Assert.Equal(original.SymmetricMode, restored.SymmetricMode);
        Assert.Equal(original.FlipH, restored.FlipH);
        Assert.Equal(original.FlipV, restored.FlipV);

        Assert.InRange(Math.Abs(original.SmoothingFactor - restored.SmoothingFactor), 0f, 0.0001f);
        Assert.InRange(Math.Abs(original.BarWidth - restored.BarWidth), 0f, 0.0001f);
        Assert.InRange(Math.Abs(original.BarSpacing - restored.BarSpacing), 0f, 0.0001f);
        Assert.InRange(Math.Abs((float)(original.Opacity - restored.Opacity)), 0f, 0.0001f);
        Assert.InRange(Math.Abs(original.Sensitivity - restored.Sensitivity), 0f, 0.0001f);
        Assert.Equal(original.MinFrequency, restored.MinFrequency);
        Assert.Equal(original.MaxFrequency, restored.MaxFrequency);
        Assert.InRange(Math.Abs(original.BarCornerRadius - restored.BarCornerRadius), 0f, 0.0001f);

        Assert.Equal(original.PrimaryColorHex, restored.PrimaryColorHex);
        Assert.InRange(Math.Abs(original.GlowIntensity - restored.GlowIntensity), 0f, 0.0001f);
        Assert.InRange(Math.Abs(original.AnimationSpeed - restored.AnimationSpeed), 0f, 0.0001f);
        Assert.Equal(original.CustomGradientColors, restored.CustomGradientColors);
        Assert.InRange(Math.Abs(original.BarGradientDarkness - restored.BarGradientDarkness), 0f, 0.0001f);
        Assert.Equal(original.BarGradientEnabled, restored.BarGradientEnabled);
        Assert.Equal(original.BarGradientBaseColorHex, restored.BarGradientBaseColorHex);
    }

    [Fact]
    public void LogoElement_RoundTrip_PreservesFlipProperties()
    {
        var original = new LogoElement
        {
            Id = "logo-1",
            Name = "My Logo",
            X = 100, Y = 50, Width = 200, Height = 180,
            ZIndex = 3,
            Rotation = 45,
            IsVisible = true,
            FlipH = true,
            FlipV = false,
            ImagePath = "/logo.png",
            Opacity = 0.75,
            ScaleMode = ScaleMode.Fit
        };

        var entity = ElementMapper.ToElement(original, "project-1");
        var restored = Assert.IsType<LogoElement>(ElementMapper.ToCanvasElement(entity));

        Assert.Equal(original.FlipH, restored.FlipH);
        Assert.Equal(original.FlipV, restored.FlipV);
        Assert.Equal(original.ImagePath, restored.ImagePath);
        Assert.InRange(Math.Abs(original.Opacity - restored.Opacity), 0d, 0.0001d);
    }

    [Fact]
    public void ImageElement_RoundTrip_PreservesFlipProperties()
    {
        var original = new ImageElement
        {
            Id = "img-1",
            Name = "My Image",
            X = 200, Y = 100, Width = 320, Height = 240,
            ZIndex = 2,
            Rotation = 0,
            IsVisible = true,
            FlipH = false,
            FlipV = true,
            FilePath = "/image.png",
            Opacity = 0.9,
            ScaleMode = ScaleMode.Fill
        };

        var entity = ElementMapper.ToElement(original, "project-1");
        var restored = Assert.IsType<ImageElement>(ElementMapper.ToCanvasElement(entity));

        Assert.Equal(original.FlipH, restored.FlipH);
        Assert.Equal(original.FlipV, restored.FlipV);
        Assert.Equal(original.FilePath, restored.FilePath);
    }

    [Fact]
    public void TextOverlayElement_RoundTrip_PreservesSizingModeAndFlipProperties()
    {
        var original = new TextOverlayElement
        {
            Id = "text-1",
            Name = "My Title",
            X = 50, Y = 30, Width = 400, Height = 100,
            ZIndex = 10,
            Rotation = 0,
            IsVisible = true,
            FlipH = true,
            FlipV = true,
            Content = "Hello World",
            Style = TextStyle.Custom,
            SizingMode = TextSizingMode.Fixed,
            FontFamily = "Arial",
            FontSize = 48,
            ColorHex = "#FFFFFF",
            IsBold = true,
            IsItalic = false,
            IsUnderline = false,
            Alignment = TextAlignment.Center,
            LineHeight = 1.5,
            LetterSpacing = 2,
            HasShadow = true,
            ShadowColorHex = "#000000",
            ShadowOffsetX = 3,
            ShadowOffsetY = 3,
            ShadowBlur = 5,
            HasOutline = false,
            HasBackground = true,
            BackgroundColorHex = "#333333",
            BackgroundOpacity = 0.7,
            BackgroundPadding = 10,
            BackgroundCornerRadius = 8
        };

        var entity = ElementMapper.ToElement(original, "project-1");
        var restored = Assert.IsType<TextOverlayElement>(ElementMapper.ToCanvasElement(entity));

        Assert.Equal(original.Content, restored.Content);
        Assert.Equal(original.SizingMode, restored.SizingMode);
        Assert.Equal(original.FlipH, restored.FlipH);
        Assert.Equal(original.FlipV, restored.FlipV);
        Assert.Equal(original.FontFamily, restored.FontFamily);
        Assert.InRange(Math.Abs(original.FontSize - restored.FontSize), 0d, 0.0001d);
        Assert.Equal(original.ColorHex, restored.ColorHex);
        Assert.Equal(original.IsBold, restored.IsBold);
        Assert.Equal(original.BackgroundColorHex, restored.BackgroundColorHex);
    }
}
