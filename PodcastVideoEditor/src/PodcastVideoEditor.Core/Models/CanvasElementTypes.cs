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
        public string ImagePath
        {
            get => _imagePath;
            set => SetProperty(ref _imagePath, value ?? string.Empty);
        }

        /// <summary>
        /// Opacity from 0.0 (transparent) to 1.0 (opaque).
        /// </summary>
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>
        /// How to scale the image (Fit, Fill, Stretch).
        /// </summary>
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

        public override ElementType Type => ElementType.Visualizer;

        /// <summary>
        /// Color palette for the visualizer.
        /// </summary>
        public ColorPalette ColorPalette
        {
            get => _colorPalette;
            set => SetProperty(ref _colorPalette, value);
        }

        /// <summary>
        /// Number of frequency bands (32, 64, 128).
        /// </summary>
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
        public VisualizerStyle Style
        {
            get => _style;
            set => SetProperty(ref _style, value);
        }

        /// <summary>
        /// Smoothing factor for bar decay (0.0 = no smoothing, 1.0 = max smoothing).
        /// </summary>
        public float SmoothingFactor
        {
            get => _smoothingFactor;
            set => SetProperty(ref _smoothingFactor, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>
        /// Whether to show peak indicators above the bars.
        /// </summary>
        public bool ShowPeaks
        {
            get => _showPeaks;
            set => SetProperty(ref _showPeaks, value);
        }

        /// <summary>
        /// Mirror mode: use only the lowest 60% of bands and display them symmetrically.
        /// </summary>
        public bool SymmetricMode
        {
            get => _symmetricMode;
            set => SetProperty(ref _symmetricMode, value);
        }

        /// <summary>
        /// How long peak indicators hold at their peak position before falling (milliseconds).
        /// </summary>
        public int PeakHoldTime
        {
            get => _peakHoldTime;
            set => SetProperty(ref _peakHoldTime, Math.Clamp(value, 0, 2000));
        }

        /// <summary>
        /// Width of each frequency bar in pixels (Bars style).
        /// </summary>
        public float BarWidth
        {
            get => _barWidth;
            set => SetProperty(ref _barWidth, Math.Clamp(value, 1f, 50f));
        }

        /// <summary>
        /// Spacing between bars in pixels.
        /// </summary>
        public float BarSpacing
        {
            get => _barSpacing;
            set => SetProperty(ref _barSpacing, Math.Clamp(value, 0f, 20f));
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
                BarSpacing = BarSpacing
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
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value ?? string.Empty);
        }

        /// <summary>
        /// Opacity from 0.0 (transparent) to 1.0 (opaque).
        /// </summary>
        public double Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>
        /// How to scale the image.
        /// </summary>
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

        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value ?? string.Empty);
        }

        /// <summary>
        /// Current style preset label. Does NOT auto-apply defaults on assignment.
        /// Call <see cref="ApplyPreset"/> to apply preset defaults.
        /// </summary>
        public TextStyle Style
        {
            get => _style;
            set => SetProperty(ref _style, value);
        }

        // ── Font ─────────────────────────────────────────────────────────────

        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value ?? "Arial");
        }

        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 200));
        }

        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        public bool IsBold
        {
            get => _isBold;
            set => SetProperty(ref _isBold, value);
        }

        public bool IsItalic
        {
            get => _isItalic;
            set => SetProperty(ref _isItalic, value);
        }

        public bool IsUnderline
        {
            get => _isUnderline;
            set => SetProperty(ref _isUnderline, value);
        }

        public TextAlignment Alignment
        {
            get => _alignment;
            set => SetProperty(ref _alignment, value);
        }

        /// <summary>Line height multiplier (1.0 = normal, 1.2 = default).</summary>
        public double LineHeight
        {
            get => _lineHeight;
            set => SetProperty(ref _lineHeight, Math.Clamp(value, 0.5, 5.0));
        }

        /// <summary>Extra spacing between characters in pixels (negative = tighter).</summary>
        public double LetterSpacing
        {
            get => _letterSpacing;
            set => SetProperty(ref _letterSpacing, Math.Clamp(value, -20, 100));
        }

        // ── Shadow ───────────────────────────────────────────────────────────

        public bool HasShadow
        {
            get => _hasShadow;
            set => SetProperty(ref _hasShadow, value);
        }

        public string ShadowColorHex
        {
            get => _shadowColorHex;
            set => SetProperty(ref _shadowColorHex, value ?? "#000000");
        }

        public float ShadowOffsetX
        {
            get => _shadowOffsetX;
            set => SetProperty(ref _shadowOffsetX, value);
        }

        public float ShadowOffsetY
        {
            get => _shadowOffsetY;
            set => SetProperty(ref _shadowOffsetY, value);
        }

        /// <summary>Shadow blur sigma (0 = sharp, higher = softer).</summary>
        public float ShadowBlur
        {
            get => _shadowBlur;
            set => SetProperty(ref _shadowBlur, Math.Clamp(value, 0f, 25f));
        }

        // ── Outline ──────────────────────────────────────────────────────────

        public bool HasOutline
        {
            get => _hasOutline;
            set => SetProperty(ref _hasOutline, value);
        }

        public string OutlineColorHex
        {
            get => _outlineColorHex;
            set => SetProperty(ref _outlineColorHex, value ?? "#000000");
        }

        public float OutlineThickness
        {
            get => _outlineThickness;
            set => SetProperty(ref _outlineThickness, Math.Clamp(value, 0.5f, 20f));
        }

        // ── Background ───────────────────────────────────────────────────────

        public bool HasBackground
        {
            get => _hasBackground;
            set => SetProperty(ref _hasBackground, value);
        }

        public string BackgroundColorHex
        {
            get => _backgroundColorHex;
            set => SetProperty(ref _backgroundColorHex, value ?? "#000000");
        }

        public double BackgroundOpacity
        {
            get => _backgroundOpacity;
            set => SetProperty(ref _backgroundOpacity, Math.Clamp(value, 0.0, 1.0));
        }

        public double BackgroundPadding
        {
            get => _backgroundPadding;
            set => SetProperty(ref _backgroundPadding, Math.Clamp(value, 0, 100));
        }

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
