#nullable enable
using SkiaSharp;
using System;
using System.IO;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Rasterizes text elements to PNG images with word-wrap, alignment, and styling.
/// Produces WYSIWYG output that matches the WPF canvas preview.
/// </summary>
public static class TextRasterizer
{
    /// <summary>
    /// Render a text element to a transparent PNG file with word-wrap support.
    /// </summary>
    /// <param name="options">Text rendering options.</param>
    /// <param name="outputPath">Path to write the PNG file.</param>
    public static void RenderToFile(TextRasterizeOptions options, string outputPath)
    {
        using var bitmap = RenderToBitmap(options);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Render a text element to an SKBitmap with word-wrap support.
    /// </summary>
    public static SKBitmap RenderToBitmap(TextRasterizeOptions options)
    {
        var width = Math.Max(1, options.Width);
        var height = Math.Max(1, options.Height);

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Draw background box if requested
        if (options.DrawBox)
        {
            using var boxPaint = new SKPaint
            {
                Color = ParseColor(options.BoxColor, options.BoxAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRect(0, 0, width, height, boxPaint);
        }

        // Configure text paint
        using var paint = new SKPaint
        {
            Color = ParseColor(options.ColorHex, 1.0f),
            IsAntialias = true,
            SubpixelText = true,
            TextSize = options.FontSize,
            Typeface = ResolveTypeface(options.FontFamily, options.IsBold, options.IsItalic)
        };

        // Calculate padding (matches WPF Padding="8,4")
        var padX = (int)Math.Round(8.0 * width / Math.Max(1, options.CanvasWidth > 0 ? options.CanvasWidth : width));
        var padY = (int)Math.Round(4.0 * height / Math.Max(1, options.CanvasHeight > 0 ? options.CanvasHeight : height));
        padX = Math.Max(2, padX);
        padY = Math.Max(1, padY);

        var textAreaWidth = width - 2 * padX;
        if (textAreaWidth <= 0)
            return bitmap;

        // Word-wrap the text
        var lines = WrapText(options.Text, paint, textAreaWidth);

        // Measure total text height
        var lineHeight = paint.FontSpacing;
        var totalTextHeight = lines.Count * lineHeight;

        // Vertical centering (matches WPF VerticalAlignment="Center")
        var startY = padY + (height - 2 * padY - totalTextHeight) / 2f + (-paint.FontMetrics.Ascent);
        if (startY < padY + (-paint.FontMetrics.Ascent))
            startY = padY + (-paint.FontMetrics.Ascent);

        // Draw each line
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineWidth = paint.MeasureText(line);

            // Horizontal alignment
            float x = options.Alignment switch
            {
                TextRasterizeAlignment.Left => padX,
                TextRasterizeAlignment.Right => width - padX - lineWidth,
                _ => padX + (textAreaWidth - lineWidth) / 2f // Center
            };

            var y = startY + i * lineHeight;
            if (y > height)
                break; // Clip text that overflows vertically

            canvas.DrawText(line, x, y, paint);
        }

        return bitmap;
    }

    /// <summary>
    /// Break text into lines that fit within the given pixel width.
    /// Handles explicit newlines (\n) and word-wrap boundaries.
    /// </summary>
    private static System.Collections.Generic.List<string> WrapText(string text, SKPaint paint, float maxWidth)
    {
        var result = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        // Split by explicit newlines first
        var paragraphs = text.Split('\n');
        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                result.Add(string.Empty);
                continue;
            }

            WrapParagraph(paragraph, paint, maxWidth, result);
        }

        return result;
    }

    /// <summary>
    /// Wrap a single paragraph (no embedded newlines) into lines.
    /// Uses greedy word-wrap: fills each line with as many words as fit.
    /// Falls back to character-level breaking for very long words.
    /// </summary>
    private static void WrapParagraph(string paragraph, SKPaint paint, float maxWidth,
        System.Collections.Generic.List<string> result)
    {
        var words = paragraph.Split(' ');
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(currentLine))
            {
                // First word on line — check if it fits
                if (paint.MeasureText(word) <= maxWidth)
                {
                    currentLine = word;
                }
                else
                {
                    // Word is wider than maxWidth — break by character
                    BreakLongWord(word, paint, maxWidth, result);
                }
            }
            else
            {
                var candidate = currentLine + " " + word;
                if (paint.MeasureText(candidate) <= maxWidth)
                {
                    currentLine = candidate;
                }
                else
                {
                    // Current line is full — flush it
                    result.Add(currentLine);
                    if (paint.MeasureText(word) <= maxWidth)
                    {
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = string.Empty;
                        BreakLongWord(word, paint, maxWidth, result);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
            result.Add(currentLine);
        else if (result.Count == 0)
            result.Add(string.Empty);
    }

    /// <summary>
    /// Break a single word that exceeds maxWidth into multiple character-level lines.
    /// </summary>
    private static void BreakLongWord(string word, SKPaint paint, float maxWidth,
        System.Collections.Generic.List<string> result)
    {
        var current = string.Empty;
        foreach (var ch in word)
        {
            var candidate = current + ch;
            if (paint.MeasureText(candidate) > maxWidth && current.Length > 0)
            {
                result.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            result.Add(current);
    }

    /// <summary>
    /// Resolve an SKTypeface from font family name and style flags.
    /// </summary>
    private static SKTypeface ResolveTypeface(string? fontFamily, bool isBold, bool isItalic)
    {
        var style = SKFontStyle.Normal;
        if (isBold && isItalic)
            style = SKFontStyle.BoldItalic;
        else if (isBold)
            style = SKFontStyle.Bold;
        else if (isItalic)
            style = SKFontStyle.Italic;

        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            var typeface = SKTypeface.FromFamilyName(fontFamily, style);
            if (typeface != null)
                return typeface;
        }

        return SKTypeface.FromFamilyName("Arial", style)
               ?? SKTypeface.Default;
    }

    /// <summary>
    /// Parse a hex color string (#RRGGBB or #AARRGGBB) to SKColor.
    /// </summary>
    private static SKColor ParseColor(string? hexColor, float alpha)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
            return new SKColor(255, 255, 255, (byte)(alpha * 255));

        var hex = hexColor.TrimStart('#');
        try
        {
            byte r, g, b, a = 255;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex[..2], 16);
                r = Convert.ToByte(hex[2..4], 16);
                g = Convert.ToByte(hex[4..6], 16);
                b = Convert.ToByte(hex[6..8], 16);
            }
            else if (hex.Length == 6)
            {
                r = Convert.ToByte(hex[..2], 16);
                g = Convert.ToByte(hex[2..4], 16);
                b = Convert.ToByte(hex[4..6], 16);
            }
            else
            {
                return new SKColor(255, 255, 255, (byte)(alpha * 255));
            }

            // Apply external alpha multiplier
            a = (byte)(a * alpha);
            return new SKColor(r, g, b, a);
        }
        catch
        {
            return new SKColor(255, 255, 255, (byte)(alpha * 255));
        }
    }
}

/// <summary>
/// Options for rendering a text element to an image.
/// </summary>
public class TextRasterizeOptions
{
    /// <summary>Text content to render.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Render width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Render height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Font size in pixels (already scaled to render resolution).</summary>
    public float FontSize { get; set; } = 48;

    /// <summary>Font family name.</summary>
    public string? FontFamily { get; set; } = "Arial";

    /// <summary>Text color as hex (#RRGGBB or #AARRGGBB).</summary>
    public string ColorHex { get; set; } = "#FFFFFF";

    /// <summary>Bold flag.</summary>
    public bool IsBold { get; set; }

    /// <summary>Italic flag.</summary>
    public bool IsItalic { get; set; }

    /// <summary>Text alignment.</summary>
    public TextRasterizeAlignment Alignment { get; set; } = TextRasterizeAlignment.Center;

    /// <summary>Whether to draw a background box.</summary>
    public bool DrawBox { get; set; } = true;

    /// <summary>Box background color hex.</summary>
    public string BoxColor { get; set; } = "#000000";

    /// <summary>Box alpha (0.0 transparent – 1.0 opaque).</summary>
    public float BoxAlpha { get; set; } = 0.5f;

    /// <summary>Canvas width for padding ratio calculation (0 = use Width).</summary>
    public double CanvasWidth { get; set; }

    /// <summary>Canvas height for padding ratio calculation (0 = use Height).</summary>
    public double CanvasHeight { get; set; }
}

/// <summary>
/// Text alignment for rasterized text.
/// </summary>
public enum TextRasterizeAlignment
{
    Left,
    Center,
    Right
}
