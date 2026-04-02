using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Captures the shared visual style for all TextOverlayElements on a text track.
/// When a user modifies any text segment's style/position on a track, the template
/// is updated and propagated to all sibling segments on the same track.
/// Only <see cref="TextOverlayElement.Content"/> remains per-segment unique.
/// </summary>
public sealed class TextStyleTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };

    // ── Position & Size ──────────────────────────────────────────────────
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 600;
    public double Height { get; set; } = 80;

    // ── Sizing ────────────────────────────────────────────────────────────
    public TextSizingMode SizingMode { get; set; } = TextSizingMode.AutoHeight;

    // ── Font ─────────────────────────────────────────────────────────────
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 32;
    public string ColorHex { get; set; } = "#FFFFFF";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public TextAlignment Alignment { get; set; } = TextAlignment.Center;
    public double LineHeight { get; set; } = 1.2;
    public double LetterSpacing { get; set; }

    // ── Style preset ─────────────────────────────────────────────────────
    public TextStyle Style { get; set; } = TextStyle.Custom;

    // ── Shadow ───────────────────────────────────────────────────────────
    public bool HasShadow { get; set; }
    public string ShadowColorHex { get; set; } = "#000000";
    public float ShadowOffsetX { get; set; } = 2f;
    public float ShadowOffsetY { get; set; } = 2f;
    public float ShadowBlur { get; set; } = 3f;

    // ── Outline ──────────────────────────────────────────────────────────
    public bool HasOutline { get; set; }
    public string OutlineColorHex { get; set; } = "#000000";
    public float OutlineThickness { get; set; } = 2f;

    // ── Background ───────────────────────────────────────────────────────
    public bool HasBackground { get; set; }
    public string BackgroundColorHex { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.5;
    public double BackgroundPadding { get; set; } = 8;
    public double BackgroundCornerRadius { get; set; } = 4;

    /// <summary>
    /// Capture all shared visual properties from a <see cref="TextOverlayElement"/>.
    /// Does NOT capture Content or SegmentId (those are per-segment).
    /// </summary>
    public static TextStyleTemplate CaptureFrom(TextOverlayElement element)
    {
        return new TextStyleTemplate
        {
            // Position & Size
            X = element.X,
            Y = element.Y,
            Width = element.Width,
            Height = element.Height,
            // Sizing
            SizingMode = element.SizingMode,
            // Font
            FontFamily = element.FontFamily,
            FontSize = element.FontSize,
            ColorHex = element.ColorHex,
            IsBold = element.IsBold,
            IsItalic = element.IsItalic,
            IsUnderline = element.IsUnderline,
            Alignment = element.Alignment,
            LineHeight = element.LineHeight,
            LetterSpacing = element.LetterSpacing,
            // Style
            Style = element.Style,
            // Shadow
            HasShadow = element.HasShadow,
            ShadowColorHex = element.ShadowColorHex,
            ShadowOffsetX = element.ShadowOffsetX,
            ShadowOffsetY = element.ShadowOffsetY,
            ShadowBlur = element.ShadowBlur,
            // Outline
            HasOutline = element.HasOutline,
            OutlineColorHex = element.OutlineColorHex,
            OutlineThickness = element.OutlineThickness,
            // Background
            HasBackground = element.HasBackground,
            BackgroundColorHex = element.BackgroundColorHex,
            BackgroundOpacity = element.BackgroundOpacity,
            BackgroundPadding = element.BackgroundPadding,
            BackgroundCornerRadius = element.BackgroundCornerRadius,
        };
    }

    /// <summary>
    /// Apply all shared visual properties to a <see cref="TextOverlayElement"/>.
    /// Preserves the element's Content, SegmentId, Id, Name, ZIndex, IsVisible, and CreatedAt.
    /// </summary>
    public void ApplyTo(TextOverlayElement element)
    {
        // Position & Size
        element.X = X;
        element.Y = Y;
        element.Width = Width;
        element.Height = Height;
        // Sizing
        element.SizingMode = SizingMode;
        // Font
        element.FontFamily = FontFamily;
        element.FontSize = FontSize;
        element.ColorHex = ColorHex;
        element.IsBold = IsBold;
        element.IsItalic = IsItalic;
        element.IsUnderline = IsUnderline;
        element.Alignment = Alignment;
        element.LineHeight = LineHeight;
        element.LetterSpacing = LetterSpacing;
        // Style
        element.Style = Style;
        // Shadow
        element.HasShadow = HasShadow;
        element.ShadowColorHex = ShadowColorHex;
        element.ShadowOffsetX = ShadowOffsetX;
        element.ShadowOffsetY = ShadowOffsetY;
        element.ShadowBlur = ShadowBlur;
        // Outline
        element.HasOutline = HasOutline;
        element.OutlineColorHex = OutlineColorHex;
        element.OutlineThickness = OutlineThickness;
        // Background
        element.HasBackground = HasBackground;
        element.BackgroundColorHex = BackgroundColorHex;
        element.BackgroundOpacity = BackgroundOpacity;
        element.BackgroundPadding = BackgroundPadding;
        element.BackgroundCornerRadius = BackgroundCornerRadius;
    }

    /// <summary>
    /// Apply only position properties (X, Y, Width, Height) to a <see cref="TextOverlayElement"/>.
    /// Used during canvas drag for performance — avoids propagating all style properties on every mouse move.
    /// </summary>
    public void ApplyPositionTo(TextOverlayElement element)
    {
        element.X = X;
        element.Y = Y;
        element.Width = Width;
        element.Height = Height;
    }

    /// <summary>Serialize to JSON for storage in Track.TextStyleJson.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserialize from JSON stored in Track.TextStyleJson. Returns null on failure.</summary>
    public static TextStyleTemplate? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TextStyleTemplate>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
