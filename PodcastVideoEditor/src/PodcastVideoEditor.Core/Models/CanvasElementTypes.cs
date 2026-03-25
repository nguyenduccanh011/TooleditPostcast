using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace PodcastVideoEditor.Core.Models
{
    /// <summary>
    /// Title element for text display with formatting options.
    /// </summary>
    public class TitleElement : CanvasElement
    {
        private string _text = "Title";
        private string _fontFamily = "Arial";
        private double _fontSize = 48;
        private string _colorHex = "#FFFFFF"; // White
        private bool _isBold;
        private bool _isItalic;
        private TextAlignment _alignment = TextAlignment.Center;

        public override ElementType Type => ElementType.Title;

        /// <summary>
        /// The text content to display.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 100, IsTextArea = true)]
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value ?? string.Empty);
        }

        /// <summary>
        /// Font family name (Arial, Verdana, etc.).
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 101)]
        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value ?? "Arial");
        }

        /// <summary>
        /// Font size in points.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 102, IsSlider = true, MinValue = 8, MaxValue = 200)]
        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 200));
        }

        /// <summary>
        /// Text color as hex string (#FFFFFF).
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 103, IsColor = true)]
        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        /// <summary>
        /// Whether text is bold.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 104)]
        public bool IsBold
        {
            get => _isBold;
            set => SetProperty(ref _isBold, value);
        }

        /// <summary>
        /// Whether text is italic.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 105)]
        public bool IsItalic
        {
            get => _isItalic;
            set => SetProperty(ref _isItalic, value);
        }

        /// <summary>
        /// Text alignment (Left, Center, Right).
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 106)]
        public TextAlignment Alignment
        {
            get => _alignment;
            set => SetProperty(ref _alignment, value);
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            Text = "Title";
            FontFamily = "Arial";
            FontSize = 48;
            ColorHex = "#FFFFFF";
            IsBold = false;
            IsItalic = false;
            Alignment = TextAlignment.Center;
        }

        public override CanvasElement Clone() =>
            new TitleElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                ZIndex = ZIndex,
                IsVisible = IsVisible,
                SegmentId = null, // Clone is independent — not bound to original segment
                Text = Text,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ColorHex = ColorHex,
                IsBold = IsBold,
                IsItalic = IsItalic,
                Alignment = Alignment
            };
    }

    /// <summary>
    /// Logo element for image display with opacity and scaling.
    /// </summary>
    public class LogoElement : CanvasElement
    {
        private string _imagePath = "";
        private double _opacity = 1.0;
        private ScaleMode _scaleMode = ScaleMode.Fit;

        public override ElementType Type => ElementType.Logo;

        /// <summary>
        /// Path to the image file.
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 100)]
        public string ImagePath
        {
            get => _imagePath;
            set => SetProperty(ref _imagePath, value ?? string.Empty);
        }

        /// <summary>
        /// Opacity from 0.0 (transparent) to 1.0 (opaque).
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 101, IsSlider = true, MinValue = 0, MaxValue = 1)]
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>
        /// How to scale the image (Fit, Fill, Stretch).
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 102)]
        public ScaleMode ScaleMode
        {
            get => _scaleMode;
            set => SetProperty(ref _scaleMode, value);
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            ImagePath = "";
            Opacity = 1.0;
            ScaleMode = ScaleMode.Fit;
        }

        public override CanvasElement Clone() =>
            new LogoElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                ZIndex = ZIndex,
                IsVisible = IsVisible,
                SegmentId = null, // Clone is independent — not bound to original segment
                ImagePath = ImagePath,
                Opacity = Opacity,
                ScaleMode = ScaleMode
            };
    }

    /// <summary>
    /// Visualizer element for spectrum visualization.
    /// </summary>
    public class VisualizerElement : CanvasElement
    {
        private ColorPalette _colorPalette = ColorPalette.Rainbow;
        private int _bandCount = 64;
        private VisualizerStyle _style = VisualizerStyle.Bars;
        private float _smoothingFactor = 0.6f;
        private bool _showPeaks = true;
        private bool _symmetricMode = true;
        private int _peakHoldTime = 300;
        private float _barWidth = 8f;
        private float _barSpacing = 2f;
        private double _opacity = 1.0;
        private float _sensitivity = 1.0f;
        private int _minFrequency = 20;
        private int _maxFrequency = 20000;
        private float _barCornerRadius = 0f;
        private string _primaryColorHex = "#00FF00";
        private float _glowIntensity = 0.5f;
        private float _animationSpeed = 1.0f;

        public override ElementType Type => ElementType.Visualizer;

        /// <summary>
        /// Color palette for the visualizer.
        /// </summary>
        [PropertyMetadata(Group = "🎨 Appearance", Order = 101)]
        public ColorPalette ColorPalette
        {
            get => _colorPalette;
            set => SetProperty(ref _colorPalette, value);
        }

        /// <summary>
        /// Number of frequency bands (32, 64, 128).
        /// </summary>
        [PropertyMetadata(Group = "📊 Frequency Bars", Order = 200, IsSlider = true, MinValue = 32, MaxValue = 128)]
        public int BandCount
        {
            get => _bandCount;
            set => SetProperty(ref _bandCount, value switch
            {
                32 or 48 or 64 or 128 => value,
                _ => 64
            });
        }

        /// <summary>
        /// Visualization style.
        /// </summary>
        [PropertyMetadata(Group = "🎨 Appearance", Order = 100)]
        public VisualizerStyle Style
        {
            get => _style;
            set => SetProperty(ref _style, value);
        }

        /// <summary>
        /// Smoothing factor for bar decay (0.0 = no smoothing, 1.0 = max smoothing).
        /// </summary>
        [PropertyMetadata(Group = "🎵 Audio Response", Order = 301, IsSlider = true, MinValue = 0, MaxValue = 1)]
        public float SmoothingFactor
        {
            get => _smoothingFactor;
            set => SetProperty(ref _smoothingFactor, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>
        /// Whether to show peak indicators above the bars.
        /// </summary>
        [PropertyMetadata(Group = "📍 Peaks", Order = 400)]
        public bool ShowPeaks
        {
            get => _showPeaks;
            set => SetProperty(ref _showPeaks, value);
        }

        /// <summary>
        /// Mirror mode: use only the lowest 60% of bands and display them symmetrically.
        /// </summary>
        [PropertyMetadata(Group = "🔄 Mode", Order = 500)]
        public bool SymmetricMode
        {
            get => _symmetricMode;
            set => SetProperty(ref _symmetricMode, value);
        }

        /// <summary>
        /// How long peak indicators hold at their peak position before falling (milliseconds).
        /// </summary>
        [PropertyMetadata(Group = "📍 Peaks", Order = 401, IsSlider = true, MinValue = 0, MaxValue = 2000)]
        public int PeakHoldTime
        {
            get => _peakHoldTime;
            set => SetProperty(ref _peakHoldTime, Math.Clamp(value, 0, 2000));
        }

        /// <summary>
        /// Width of each frequency bar in pixels (Bars style).
        /// </summary>
        [PropertyMetadata(Group = "📊 Frequency Bars", Order = 201, IsSlider = true, MinValue = 1, MaxValue = 50)]
        public float BarWidth
        {
            get => _barWidth;
            set => SetProperty(ref _barWidth, Math.Clamp(value, 1f, 50f));
        }

        /// <summary>
        /// Spacing between bars in pixels.
        /// </summary>
        [PropertyMetadata(Group = "📊 Frequency Bars", Order = 202, IsSlider = true, MinValue = 0, MaxValue = 20)]
        public float BarSpacing
        {
            get => _barSpacing;
            set => SetProperty(ref _barSpacing, Math.Clamp(value, 0f, 20f));
        }

        /// <summary>
        /// Opacity from 0.0 (transparent) to 1.0 (opaque).
        /// </summary>
        [PropertyMetadata(Group = "🎨 Appearance", Order = 103, IsSlider = true, MinValue = 0, MaxValue = 1)]
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>
        /// Audio reactivity gain multiplier (0.5 = subtle, 3.0 = very reactive).
        /// </summary>
        [PropertyMetadata(Group = "🎵 Audio Response", Order = 300, IsSlider = true, MinValue = 0.1, MaxValue = 3)]
        public float Sensitivity
        {
            get => _sensitivity;
            set => SetProperty(ref _sensitivity, Math.Clamp(value, 0.1f, 3.0f));
        }

        /// <summary>
        /// Low frequency cutoff in Hz for spectrum analysis.
        /// </summary>
        [PropertyMetadata(Group = "🎵 Audio Response", Order = 302, IsSlider = true, MinValue = 20, MaxValue = 2000)]
        public int MinFrequency
        {
            get => _minFrequency;
            set => SetProperty(ref _minFrequency, Math.Clamp(value, 20, 2000));
        }

        /// <summary>
        /// High frequency cutoff in Hz for spectrum analysis.
        /// </summary>
        [PropertyMetadata(Group = "🎵 Audio Response", Order = 303, IsSlider = true, MinValue = 2000, MaxValue = 20000)]
        public int MaxFrequency
        {
            get => _maxFrequency;
            set => SetProperty(ref _maxFrequency, Math.Clamp(value, 2000, 20000));
        }

        /// <summary>
        /// Corner radius for frequency bars (0 = sharp, 20 = fully rounded).
        /// </summary>
        [PropertyMetadata(Group = "📊 Frequency Bars", Order = 203, IsSlider = true, MinValue = 0, MaxValue = 20)]
        public float BarCornerRadius
        {
            get => _barCornerRadius;
            set => SetProperty(ref _barCornerRadius, Math.Clamp(value, 0f, 20f));
        }

        /// <summary>
        /// Custom primary color hex for Mono palette or color override (#RRGGBB).
        /// </summary>
        [PropertyMetadata(Group = "🎨 Appearance", Order = 102, IsColor = true)]
        public string PrimaryColorHex
        {
            get => _primaryColorHex;
            set => SetProperty(ref _primaryColorHex, value ?? "#00FF00");
        }

        /// <summary>
        /// Glow effect intensity (0 = none, 2.0 = maximum). Primarily for NeonGlow style.
        /// </summary>
        [PropertyMetadata(Group = "🎨 Appearance", Order = 104, IsSlider = true, MinValue = 0, MaxValue = 2)]
        public float GlowIntensity
        {
            get => _glowIntensity;
            set => SetProperty(ref _glowIntensity, Math.Clamp(value, 0f, 2.0f));
        }

        /// <summary>
        /// Animation/decay speed multiplier (0.1 = very slow, 3.0 = very fast).
        /// </summary>
        [PropertyMetadata(Group = "🎵 Audio Response", Order = 304, IsSlider = true, MinValue = 0.1, MaxValue = 3)]
        public float AnimationSpeed
        {
            get => _animationSpeed;
            set => SetProperty(ref _animationSpeed, Math.Clamp(value, 0.1f, 3.0f));
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            ColorPalette = ColorPalette.Rainbow;
            BandCount = 64;
            Style = VisualizerStyle.Bars;
            SmoothingFactor = 0.6f;
            ShowPeaks = true;
            SymmetricMode = true;
            PeakHoldTime = 300;
            BarWidth = 8f;
            BarSpacing = 2f;
            Opacity = 1.0;
            Sensitivity = 1.0f;
            MinFrequency = 20;
            MaxFrequency = 20000;
            BarCornerRadius = 0f;
            PrimaryColorHex = "#00FF00";
            GlowIntensity = 0.5f;
            AnimationSpeed = 1.0f;
        }

        public override CanvasElement Clone() =>
            new VisualizerElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                ZIndex = ZIndex,
                IsVisible = IsVisible,
                SegmentId = null, // Clone is independent — not bound to original segment
                ColorPalette = ColorPalette,
                BandCount = BandCount,
                Style = Style,
                SmoothingFactor = SmoothingFactor,
                ShowPeaks = ShowPeaks,
                SymmetricMode = SymmetricMode,
                PeakHoldTime = PeakHoldTime,
                BarWidth = BarWidth,
                BarSpacing = BarSpacing,
                Opacity = Opacity,
                Sensitivity = Sensitivity,
                MinFrequency = MinFrequency,
                MaxFrequency = MaxFrequency,
                BarCornerRadius = BarCornerRadius,
                PrimaryColorHex = PrimaryColorHex,
                GlowIntensity = GlowIntensity,
                AnimationSpeed = AnimationSpeed
            };
    }

    /// <summary>
    /// Image element for static image display.
    /// </summary>
    public class ImageElement : CanvasElement
    {
        private string _filePath = "";
        private double _opacity = 1.0;
        private ScaleMode _scaleMode = ScaleMode.Fill;

        public override ElementType Type => ElementType.Image;

        /// <summary>
        /// Path to the image file.
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 100)]
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value ?? string.Empty);
        }

        /// <summary>
        /// Opacity from 0.0 (transparent) to 1.0 (opaque).
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 101, IsSlider = true, MinValue = 0, MaxValue = 1)]
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>
        /// How to scale the image.
        /// </summary>
        [PropertyMetadata(Group = "🖼 Image", Order = 102)]
        public ScaleMode ScaleMode
        {
            get => _scaleMode;
            set => SetProperty(ref _scaleMode, value);
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            FilePath = "";
            Opacity = 1.0;
            ScaleMode = ScaleMode.Fill;
        }

        public override CanvasElement Clone() =>
            new ImageElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                ZIndex = ZIndex,
                IsVisible = IsVisible,
                SegmentId = null, // Clone is independent — not bound to original segment
                FilePath = FilePath,
                Opacity = Opacity,
                ScaleMode = ScaleMode
            };
    }

    /// <summary>
    /// Generic text element with formatting.
    /// </summary>
    public class TextElement : CanvasElement
    {
        private string _content = "Text";
        private string _fontFamily = "Arial";
        private double _fontSize = 24;
        private string _colorHex = "#FFFFFF"; // White
        private bool _isBold;
        private bool _isItalic;
        private TextAlignment _alignment = TextAlignment.Center;

        public override ElementType Type => ElementType.Text;

        /// <summary>
        /// Text content.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 100, IsTextArea = true)]
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value ?? string.Empty);
        }

        /// <summary>
        /// Font family name (Arial, Verdana, etc.).
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 101)]
        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value ?? "Arial");
        }

        /// <summary>
        /// Font size in points.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 102, IsSlider = true, MinValue = 8, MaxValue = 200)]
        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 200));
        }

        /// <summary>
        /// Text color as hex string.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 103, IsColor = true)]
        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        /// <summary>
        /// Whether text is bold.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 104)]
        public bool IsBold
        {
            get => _isBold;
            set => SetProperty(ref _isBold, value);
        }

        /// <summary>
        /// Whether text is italic.
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 105)]
        public bool IsItalic
        {
            get => _isItalic;
            set => SetProperty(ref _isItalic, value);
        }

        /// <summary>
        /// Text alignment (Left, Center, Right).
        /// </summary>
        [PropertyMetadata(Group = "✍ Text", Order = 106)]
        public TextAlignment Alignment
        {
            get => _alignment;
            set => SetProperty(ref _alignment, value);
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            Content = "Text";
            FontFamily = "Arial";
            FontSize = 24;
            ColorHex = "#FFFFFF";
            IsBold = false;
            IsItalic = false;
            Alignment = TextAlignment.Center;
        }

        public override CanvasElement Clone() =>
            new TextElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                ZIndex = ZIndex,
                IsVisible = IsVisible,
                SegmentId = null, // Clone is independent — not bound to original segment
                Content = Content,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ColorHex = ColorHex,
                IsBold = IsBold,
                IsItalic = IsItalic,
                Alignment = Alignment
            };
    }

    /// <summary>
    /// Image scaling modes.
    /// </summary>
    public enum ScaleMode
    {
        /// <summary>
        /// Scale image to fit within bounds, preserving aspect ratio.
        /// </summary>
        Fit,

        /// <summary>
        /// Scale image to fill bounds, preserving aspect ratio, may crop.
        /// </summary>
        Fill,

        /// <summary>
        /// Scale image to exactly match bounds, may distort.
        /// </summary>
        Stretch
    }

    /// <summary>
    /// Visualizer color palettes.
    /// </summary>
    public enum ColorPalette
    {
        Rainbow,
        Fire,
        Ocean,
        Mono,
        Purple
    }

    /// <summary>
    /// Visualizer display styles.
    /// </summary>
    public enum VisualizerStyle
    {
        Bars,
        Waveform,
        Circular,
        NeonGlow,
        Particles,
        Ring,
        LineWave
    }

    /// <summary>
    /// Text alignment options.
    /// </summary>
    public enum TextAlignment
    {
        Left,
        Center,
        Right
    }
}
