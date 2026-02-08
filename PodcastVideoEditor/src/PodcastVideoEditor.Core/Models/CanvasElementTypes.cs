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
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value ?? string.Empty);
        }

        /// <summary>
        /// Font family name (Arial, Verdana, etc.).
        /// </summary>
        public string FontFamily
        {
            get => _fontFamily;
            set => SetProperty(ref _fontFamily, value ?? "Arial");
        }

        /// <summary>
        /// Font size in points.
        /// </summary>
        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 200));
        }

        /// <summary>
        /// Text color as hex string (#FFFFFF).
        /// </summary>
        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        /// <summary>
        /// Whether text is bold.
        /// </summary>
        public bool IsBold
        {
            get => _isBold;
            set => SetProperty(ref _isBold, value);
        }

        /// <summary>
        /// Whether text is italic.
        /// </summary>
        public bool IsItalic
        {
            get => _isItalic;
            set => SetProperty(ref _isItalic, value);
        }

        /// <summary>
        /// Text alignment (Left, Center, Right).
        /// </summary>
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
                32 or 64 or 128 => value,
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

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            ColorPalette = ColorPalette.Rainbow;
            BandCount = 64;
            Style = VisualizerStyle.Bars;
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
                ColorPalette = ColorPalette,
                BandCount = BandCount,
                Style = Style
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
        private double _fontSize = 24;
        private string _colorHex = "#FFFFFF"; // White

        public override ElementType Type => ElementType.Text;

        /// <summary>
        /// Text content.
        /// </summary>
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value ?? string.Empty);
        }

        /// <summary>
        /// Font size in points.
        /// </summary>
        public double FontSize
        {
            get => _fontSize;
            set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 100));
        }

        /// <summary>
        /// Text color as hex string.
        /// </summary>
        public string ColorHex
        {
            get => _colorHex;
            set => SetProperty(ref _colorHex, value ?? "#FFFFFF");
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();
            Content = "Text";
            FontSize = 24;
            ColorHex = "#FFFFFF";
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
                Content = Content,
                FontSize = FontSize,
                ColorHex = ColorHex
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
        Circular
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
