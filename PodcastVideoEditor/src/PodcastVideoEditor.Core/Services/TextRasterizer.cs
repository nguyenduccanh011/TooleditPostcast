#nullable enable
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Rasterizes text elements to PNG images with word-wrap, alignment, and styling.
/// Produces WYSIWYG output that matches the WPF canvas preview.
/// </summary>
public static class TextRasterizer
{
    // ── Typeface cache ──────────────────────────────────────────────────
    // SKTypeface.FromFamilyName() is expensive (~0.5-2ms per call) because it
    // queries the OS font registry. Cache resolved typefaces per (family, style)
    // tuple so Parallel.For text rasterization reuses them across segments.
    private static readonly ConcurrentDictionary<(string family, SKFontStyle style), SKTypeface> _typefaceCache = new();

    /// <summary>
    /// Render a text element to a transparent PNG file with word-wrap support.
    /// Uses PNG quality 80 (sufficient for temp overlay — FFmpeg re-decodes immediately).
    /// </summary>
    /// <param name="options">Text rendering options.</param>
    /// <param name="outputPath">Path to write the PNG file.</param>
    public static void RenderToFile(TextRasterizeOptions options, string outputPath)
    {
        using var bitmap = RenderToBitmap(options);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Render a text element to an SKBitmap with word-wrap support.
    /// Supports shadow, outline stroke, background corner radius, line height, and letter spacing.
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
            var boxRect = new SKRect(0, 0, width, height);
            if (options.BackgroundCornerRadius > 0)
                canvas.DrawRoundRect(boxRect, options.BackgroundCornerRadius, options.BackgroundCornerRadius, boxPaint);
            else
                canvas.DrawRect(boxRect, boxPaint);
        }

        var typeface = ResolveTypeface(options.FontFamily, options.IsBold, options.IsItalic);

        // Configure fill paint
        using var paint = new SKPaint
        {
            Color = ParseColor(options.ColorHex, 1.0f),
            IsAntialias = true,
            SubpixelText = true,
            TextSize = options.FontSize,
            Typeface = typeface
        };

        // Calculate padding (matches WPF Padding="8,4")
        var padX = (int)Math.Round(8.0 * width / Math.Max(1, options.CanvasWidth > 0 ? options.CanvasWidth : width));
        var padY = (int)Math.Round(4.0 * height / Math.Max(1, options.CanvasHeight > 0 ? options.CanvasHeight : height));
        padX = Math.Max(2, padX);
        padY = Math.Max(1, padY);

        var textAreaWidth = width - 2 * padX;
        if (textAreaWidth <= 0)
        {
            // Return transparent bitmap — no room to render text
            bitmap.Dispose();
            var emptyBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var c = new SKCanvas(emptyBitmap);
            c.Clear(SKColors.Transparent);
            return emptyBitmap;
        }

        // Word-wrap the text (letter spacing affects effective width)
        var lines = WrapText(options.Text, paint, textAreaWidth, options.LetterSpacing);

        // Line height with multiplier
        var lineHeight = paint.FontSpacing * options.LineHeightMultiplier;
        var totalTextHeight = lines.Count * lineHeight;

        // Top-aligned rendering (matches WPF VerticalAlignment="Top")
        var startY = padY + (-paint.FontMetrics.Ascent);

        // Pre-allocate shadow & outline paints once (reused across all lines)
        SKPaint? shadowPaint = null;
        SKPaint? outlinePaint = null;
        try
        {
            if (options.HasShadow)
            {
                shadowPaint = new SKPaint
                {
                    Color = ParseColor(options.ShadowColorHex, 1.0f),
                    IsAntialias = true,
                    SubpixelText = true,
                    TextSize = options.FontSize,
                    Typeface = typeface
                };
                if (options.ShadowBlur > 0)
                    shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, options.ShadowBlur);
            }

            if (options.HasOutline && options.OutlineThickness > 0)
            {
                outlinePaint = new SKPaint
                {
                    Color = ParseColor(options.OutlineColorHex, 1.0f),
                    IsAntialias = true,
                    SubpixelText = true,
                    TextSize = options.FontSize,
                    Typeface = typeface,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = options.OutlineThickness,
                    StrokeJoin = SKStrokeJoin.Round
                };
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var lineWidth = MeasureLineWidth(line, paint, options.LetterSpacing);

                float x = options.Alignment switch
                {
                    TextRasterizeAlignment.Left => padX,
                    TextRasterizeAlignment.Right => width - padX - lineWidth,
                    _ => padX + (textAreaWidth - lineWidth) / 2f
                };

                var y = startY + i * lineHeight;
                if (y > height) break;

                // 1. Shadow pass
                if (shadowPaint != null)
                    DrawLine(canvas, line, x + options.ShadowOffsetX, y + options.ShadowOffsetY, shadowPaint, options.LetterSpacing);

                // 2. Outline stroke pass (drawn before fill so fill sits on top)
                if (outlinePaint != null)
                    DrawLine(canvas, line, x, y, outlinePaint, options.LetterSpacing);

                // 3. Fill pass
                DrawLine(canvas, line, x, y, paint, options.LetterSpacing);
            }
        }
        finally
        {
            shadowPaint?.Dispose();
            outlinePaint?.Dispose();
        }

        return bitmap;
    }

    /// <summary>Draw a single line with optional per-character letter spacing.</summary>
    private static void DrawLine(SKCanvas canvas, string line, float x, float y, SKPaint paint, float letterSpacing)
    {
        if (letterSpacing == 0 || line.Length <= 1)
        {
            canvas.DrawText(line, x, y, paint);
            return;
        }

        // Batch letter-spaced text via SKTextBlob — single draw call instead
        // of N separate DrawText calls. Build glyph array + positions up-front.
        using var font = paint.ToFont();
        font.Size = paint.TextSize;
        font.Typeface = paint.Typeface;

        var advances = paint.GetGlyphWidths(line);
        var glyphs = new ushort[line.Length];
        var positions = new SKPoint[line.Length];
        float cx = x;
        for (int i = 0; i < line.Length; i++)
        {
            glyphs[i] = font.GetGlyph(line[i]);
            positions[i] = new SKPoint(cx, y);
            cx += advances[i] + letterSpacing;
        }

        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, glyphs.Length);
        glyphs.AsSpan().CopyTo(run.GetGlyphSpan());
        positions.AsSpan().CopyTo(run.GetPositionSpan());
        using var blob = builder.Build();
        if (blob != null)
            canvas.DrawText(blob, 0, 0, paint);
    }

    /// <summary>Measure the rendered width of a line, accounting for letter spacing.</summary>
    private static float MeasureLineWidth(string line, SKPaint paint, float letterSpacing)
    {
        if (letterSpacing == 0 || line.Length <= 1)
            return paint.MeasureText(line);

        // Use paint glyph widths for accurate measurement
        var widths = paint.GetGlyphWidths(line);
        float total = 0;
        for (int i = 0; i < widths.Length; i++)
            total += widths[i] + letterSpacing;
        // Remove trailing spacing from last char
        return total - letterSpacing;
    }

    /// <summary>
    /// Break text into lines that fit within the given pixel width.
    /// Handles explicit newlines (\n) and word-wrap boundaries.
    /// </summary>
    private static System.Collections.Generic.List<string> WrapText(string text, SKPaint paint, float maxWidth, float letterSpacing = 0)
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

            WrapParagraph(paragraph, paint, maxWidth, result, letterSpacing);
        }

        return result;
    }

    /// <summary>
    /// Wrap a single paragraph (no embedded newlines) into lines.
    /// Uses greedy word-wrap: fills each line with as many words as fit.
    /// Falls back to character-level breaking for very long words.
    /// </summary>
    private static void WrapParagraph(string paragraph, SKPaint paint, float maxWidth,
        System.Collections.Generic.List<string> result, float letterSpacing = 0)
    {
        var words = paragraph.Split(' ');
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(currentLine))
            {
                // First word on line — check if it fits
                if (MeasureLineWidth(word, paint, letterSpacing) <= maxWidth)
                {
                    currentLine = word;
                }
                else
                {
                    // Word is wider than maxWidth — break by character
                    BreakLongWord(word, paint, maxWidth, result, letterSpacing);
                }
            }
            else
            {
                var candidate = currentLine + " " + word;
                if (MeasureLineWidth(candidate, paint, letterSpacing) <= maxWidth)
                {
                    currentLine = candidate;
                }
                else
                {
                    // Current line is full — flush it
                    result.Add(currentLine);
                    if (MeasureLineWidth(word, paint, letterSpacing) <= maxWidth)
                    {
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = string.Empty;
                        BreakLongWord(word, paint, maxWidth, result, letterSpacing);
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
        System.Collections.Generic.List<string> result, float letterSpacing = 0)
    {
        var current = string.Empty;
        foreach (var ch in word)
        {
            var candidate = current + ch;
            if (MeasureLineWidth(candidate, paint, letterSpacing) > maxWidth && current.Length > 0)
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
    /// Results are cached in a static ConcurrentDictionary for reuse across
    /// parallel text rasterization calls.
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

        var family = fontFamily ?? "Arial";
        return _typefaceCache.GetOrAdd((family, style), key =>
        {
            if (!string.IsNullOrWhiteSpace(key.family))
            {
                var typeface = SKTypeface.FromFamilyName(key.family, key.style);
                if (typeface != null)
                    return typeface;
            }
            return SKTypeface.FromFamilyName("Arial", key.style) ?? SKTypeface.Default;
        });
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

    /// <summary>Corner radius for the background box in pixels.</summary>
    public float BackgroundCornerRadius { get; set; }

    // -- Shadow -------------------------------------------------------------------

    /// <summary>Enable drop shadow.</summary>
    public bool HasShadow { get; set; }

    /// <summary>Shadow color hex.</summary>
    public string ShadowColorHex { get; set; } = "#000000";

    /// <summary>Horizontal shadow offset in pixels.</summary>
    public float ShadowOffsetX { get; set; } = 2f;

    /// <summary>Vertical shadow offset in pixels.</summary>
    public float ShadowOffsetY { get; set; } = 2f;

    /// <summary>Shadow blur sigma (0 = sharp).</summary>
    public float ShadowBlur { get; set; } = 3f;

    // -- Outline ------------------------------------------------------------------

    /// <summary>Enable text outline/stroke.</summary>
    public bool HasOutline { get; set; }

    /// <summary>Outline color hex.</summary>
    public string OutlineColorHex { get; set; } = "#000000";

    /// <summary>Outline stroke thickness in pixels.</summary>
    public float OutlineThickness { get; set; } = 2f;

    // -- Spacing ------------------------------------------------------------------

    /// <summary>Line height multiplier (1.0 = normal, 1.2 = default).</summary>
    public float LineHeightMultiplier { get; set; } = 1.2f;

    /// <summary>Extra space between characters in pixels (negative = tighter).</summary>
    public float LetterSpacing { get; set; }

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
