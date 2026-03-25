using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace PodcastVideoEditor.Core.Models
{


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

    // -------------------------------------------------------------------------
    // Unified text overlay
    // -------------------------------------------------------------------------

    /// <summary>
    /// Style preset for <see cref="TextOverlayElement"/>.
    /// Calling <see cref="TextOverlayElement.ApplyPreset"/> with one of these values
    /// populates sensible defaults. Change individual properties afterwards to customise.
    /// </summary>
    public enum TextStyle
    {
        Custom,
        Title,
        Subtitle,
        Caption,
        LowerThird
    }

    /// <summary>
    /// Unified, feature-rich text overlay element.
    /// Supports shadow, outline, background box, line-height, and letter-spacing.
    /// </summary>
    public class TextOverlayElement : CanvasElement
    {
        // ── Content ──────────────────────────────────────────────────────────
        private string _content = "";
        private TextStyle _style = TextStyle.Custom;

        // ── Font ─────────────────────────────────────────────────────────────
        private string _fontFamily = "Arial";
        private double _fontSize = 32;
        private string _colorHex = "#FFFFFF";
        private bool _isBold;
        private bool _isItalic;
        private bool _isUnderline;
        private TextAlignment _alignment = TextAlignment.Center;
        private double _lineHeight = 1.2;
        private double _letterSpacing;

        // ── Shadow ───────────────────────────────────────────────────────────
        private bool _hasShadow;
        private string _shadowColorHex = "#000000";
        private float _shadowOffsetX = 2f;
        private float _shadowOffsetY = 2f;
        private float _shadowBlur = 3f;

        // ── Outline ──────────────────────────────────────────────────────────
        private bool _hasOutline;
        private string _outlineColorHex = "#000000";
        private float _outlineThickness = 2f;

        // ── Background ───────────────────────────────────────────────────────
        private bool _hasBackground;
        private string _backgroundColorHex = "#000000";
        private double _backgroundOpacity = 0.5;
        private double _backgroundPadding = 8;
        private double _backgroundCornerRadius = 4;

        public override ElementType Type => ElementType.TextOverlay;

        // ── Content ──────────────────────────────────────────────────────────

        [PropertyMetadata(Group = "✍️ Text", Order = 100, IsTextArea = true)]
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value ?? string.Empty);
        }

        /// <summary>
        /// Current style preset label. Does NOT auto-apply defaults on assignment.
        /// Call <see cref="ApplyPreset"/> to apply preset defaults.
        /// </summary>
        [PropertyMetadata(Group = "✍️ Text", Order = 107)]
        public TextStyle Style
        {
            get => _style;
            set => SetProperty(ref _style, value);
        }

        // ── Font ─────────────────────────────────────────────────────────────

        [PropertyMetadata(Group = "✍️ Text", Order = 101)]
        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value ?? "Arial");
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 102, IsSlider = true, MinValue = 8, MaxValue = 200)]
        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 200));
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 103, IsColor = true)]
        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 104)]
        public bool IsBold
        {
            get => _isBold;
            set => SetProperty(ref _isBold, value);
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 105)]
        public bool IsItalic
        {
            get => _isItalic;
            set => SetProperty(ref _isItalic, value);
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 106)]
        public bool IsUnderline
        {
            get => _isUnderline;
            set => SetProperty(ref _isUnderline, value);
        }

        [PropertyMetadata(Group = "✍️ Text", Order = 108)]
        public TextAlignment Alignment
        {
            get => _alignment;
            set => SetProperty(ref _alignment, value);
        }

        /// <summary>Line height multiplier (1.0 = normal, 1.2 = default).</summary>
        [PropertyMetadata(Group = "✍️ Text", Order = 109, IsSlider = true, MinValue = 0.5, MaxValue = 5)]
        public double LineHeight
        {
            get => _lineHeight;
            set => SetProperty(ref _lineHeight, Math.Clamp(value, 0.5, 5.0));
        }

        /// <summary>Extra spacing between characters in pixels (negative = tighter).</summary>
        [PropertyMetadata(Group = "✍️ Text", Order = 110, IsSlider = true, MinValue = -20, MaxValue = 100)]
        public double LetterSpacing
        {
            get => _letterSpacing;
            set => SetProperty(ref _letterSpacing, Math.Clamp(value, -20, 100));
        }

        // ── Shadow ───────────────────────────────────────────────────────────

        [PropertyMetadata(Group = "👤 Shadow", Order = 200)]
        public bool HasShadow
        {
            get => _hasShadow;
            set => SetProperty(ref _hasShadow, value);
        }

        [PropertyMetadata(Group = "👤 Shadow", Order = 201, IsColor = true)]
        public string ShadowColorHex
        {
            get => _shadowColorHex;
            set => SetProperty(ref _shadowColorHex, value ?? "#000000");
        }

        [PropertyMetadata(Group = "👤 Shadow", Order = 202, IsSlider = true, MinValue = -20, MaxValue = 20)]
        public float ShadowOffsetX
        {
            get => _shadowOffsetX;
            set => SetProperty(ref _shadowOffsetX, value);
        }

        [PropertyMetadata(Group = "👤 Shadow", Order = 203, IsSlider = true, MinValue = -20, MaxValue = 20)]
        public float ShadowOffsetY
        {
            get => _shadowOffsetY;
            set => SetProperty(ref _shadowOffsetY, value);
        }

        /// <summary>Shadow blur sigma (0 = sharp, higher = softer).</summary>
        [PropertyMetadata(Group = "👤 Shadow", Order = 204, IsSlider = true, MinValue = 0, MaxValue = 25)]
        public float ShadowBlur
        {
            get => _shadowBlur;
            set => SetProperty(ref _shadowBlur, Math.Clamp(value, 0f, 25f));
        }

        // ── Outline ──────────────────────────────────────────────────────────

        [PropertyMetadata(Group = "□ Outline", Order = 300)]
        public bool HasOutline
        {
            get => _hasOutline;
            set => SetProperty(ref _hasOutline, value);
        }

        [PropertyMetadata(Group = "□ Outline", Order = 301, IsColor = true)]
        public string OutlineColorHex
        {
            get => _outlineColorHex;
            set => SetProperty(ref _outlineColorHex, value ?? "#000000");
        }

        [PropertyMetadata(Group = "□ Outline", Order = 302, IsSlider = true, MinValue = 0.5, MaxValue = 20)]
        public float OutlineThickness
        {
            get => _outlineThickness;
            set => SetProperty(ref _outlineThickness, Math.Clamp(value, 0.5f, 20f));
        }

        // ── Background ───────────────────────────────────────────────────────

        [PropertyMetadata(Group = "🟦 Background", Order = 400)]
        public bool HasBackground
        {
            get => _hasBackground;
            set => SetProperty(ref _hasBackground, value);
        }

        [PropertyMetadata(Group = "🟦 Background", Order = 401, IsColor = true)]
        public string BackgroundColorHex
        {
            get => _backgroundColorHex;
            set => SetProperty(ref _backgroundColorHex, value ?? "#000000");
        }

        [PropertyMetadata(Group = "🟦 Background", Order = 402, IsSlider = true, MinValue = 0, MaxValue = 1)]
        public double BackgroundOpacity
        {
            get => _backgroundOpacity;
            set => SetProperty(ref _backgroundOpacity, Math.Clamp(value, 0.0, 1.0));
        }

        [PropertyMetadata(Group = "🟦 Background", Order = 403, IsSlider = true, MinValue = 0, MaxValue = 100)]
        public double BackgroundPadding
        {
            get => _backgroundPadding;
            set => SetProperty(ref _backgroundPadding, Math.Clamp(value, 0, 100));
        }

        [PropertyMetadata(Group = "🟦 Background", Order = 404, IsSlider = true, MinValue = 0, MaxValue = 50)]
        public double BackgroundCornerRadius
        {
            get => _backgroundCornerRadius;
            set => SetProperty(ref _backgroundCornerRadius, Math.Clamp(value, 0, 50));
        }

        // ── Preset ───────────────────────────────────────────────────────────

        /// <summary>
        /// Applies default property values for the given style preset.
        /// Individual properties can be customised afterwards.
        /// </summary>
        public void ApplyPreset(TextStyle preset)
        {
            Style = preset;
            switch (preset)
            {
                case TextStyle.Title:
                    FontSize = 48; IsBold = true; ColorHex = "#FFFFFF";
                    Alignment = TextAlignment.Center;
                    HasShadow = true; ShadowOffsetX = 2; ShadowOffsetY = 2; ShadowBlur = 4;
                    HasOutline = false;
                    HasBackground = false;
                    break;

                case TextStyle.Subtitle:
                    FontSize = 28; IsBold = false; ColorHex = "#EEEEEE";
                    Alignment = TextAlignment.Center;
                    HasShadow = true; ShadowOffsetX = 1; ShadowOffsetY = 1; ShadowBlur = 2;
                    HasOutline = false;
                    HasBackground = false;
                    break;

                case TextStyle.Caption:
                    FontSize = 18; IsBold = false; ColorHex = "#FFFFFF";
                    Alignment = TextAlignment.Center;
                    HasShadow = false;
                    HasOutline = false;
                    HasBackground = true; BackgroundColorHex = "#000000";
                    BackgroundOpacity = 0.6; BackgroundPadding = 8; BackgroundCornerRadius = 0;
                    break;

                case TextStyle.LowerThird:
                    FontSize = 24; IsBold = true; ColorHex = "#FFFFFF";
                    Alignment = TextAlignment.Left;
                    HasShadow = false;
                    HasOutline = false;
                    HasBackground = true; BackgroundColorHex = "#003366";
                    BackgroundOpacity = 0.85; BackgroundPadding = 12; BackgroundCornerRadius = 4;
                    break;
            }
        }

        // ── CanvasElement overrides ───────────────────────────────────────────

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            Content = ""; Style = TextStyle.Custom;
            FontFamily = "Arial"; FontSize = 32; ColorHex = "#FFFFFF";
            IsBold = false; IsItalic = false; IsUnderline = false;
            Alignment = TextAlignment.Center; LineHeight = 1.2; LetterSpacing = 0;
            HasShadow = false; ShadowColorHex = "#000000";
            ShadowOffsetX = 2; ShadowOffsetY = 2; ShadowBlur = 3;
            HasOutline = false; OutlineColorHex = "#000000"; OutlineThickness = 2;
            HasBackground = false; BackgroundColorHex = "#000000";
            BackgroundOpacity = 0.5; BackgroundPadding = 8; BackgroundCornerRadius = 4;
        }

        public override CanvasElement Clone() =>
            new TextOverlayElement
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                X = X, Y = Y, Width = Width, Height = Height,
                ZIndex = ZIndex, IsVisible = IsVisible,
                SegmentId = null,
                Content = Content, Style = TextStyle.Custom,
                FontFamily = FontFamily, FontSize = FontSize, ColorHex = ColorHex,
                IsBold = IsBold, IsItalic = IsItalic, IsUnderline = IsUnderline,
                Alignment = Alignment, LineHeight = LineHeight, LetterSpacing = LetterSpacing,
                HasShadow = HasShadow, ShadowColorHex = ShadowColorHex,
                ShadowOffsetX = ShadowOffsetX, ShadowOffsetY = ShadowOffsetY, ShadowBlur = ShadowBlur,
                HasOutline = HasOutline, OutlineColorHex = OutlineColorHex, OutlineThickness = OutlineThickness,
                HasBackground = HasBackground, BackgroundColorHex = BackgroundColorHex,
                BackgroundOpacity = BackgroundOpacity, BackgroundPadding = BackgroundPadding,
                BackgroundCornerRadius = BackgroundCornerRadius
            };
    }
}
