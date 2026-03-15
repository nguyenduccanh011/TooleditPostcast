#nullable enable
using System;
using System.Globalization;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Maps canvas element positions (pixels in preview space) to FFmpeg filter expressions
/// for the render output. Handles coordinate scaling between canvas and render resolution.
/// </summary>
public static class CoordinateMapper
{
    /// <summary>
    /// Convert a canvas element's position/size to FFmpeg-compatible integer coordinates
    /// in the render output space.
    /// </summary>
    /// <param name="canvasX">Element X position in canvas pixels.</param>
    /// <param name="canvasY">Element Y position in canvas pixels.</param>
    /// <param name="canvasW">Element width in canvas pixels.</param>
    /// <param name="canvasH">Element height in canvas pixels.</param>
    /// <param name="canvasWidth">Total canvas width (preview resolution).</param>
    /// <param name="canvasHeight">Total canvas height (preview resolution).</param>
    /// <param name="renderWidth">Render output width in pixels.</param>
    /// <param name="renderHeight">Render output height in pixels.</param>
    /// <returns>Scaled coordinates in render space.</returns>
    public static (int X, int Y, int Width, int Height) MapToRender(
        double canvasX, double canvasY, double canvasW, double canvasH,
        double canvasWidth, double canvasHeight,
        int renderWidth, int renderHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
            return (0, 0, renderWidth, renderHeight);

        var scaleX = renderWidth / canvasWidth;
        var scaleY = renderHeight / canvasHeight;

        return (
            X: (int)Math.Round(canvasX * scaleX),
            Y: (int)Math.Round(canvasY * scaleY),
            Width: Math.Max(2, (int)Math.Round(canvasW * scaleX)),
            Height: Math.Max(2, (int)Math.Round(canvasH * scaleY))
        );
    }

    /// <summary>
    /// Convert canvas position to FFmpeg expression strings for drawtext x/y parameters.
    /// </summary>
    public static (string XExpr, string YExpr) ToTextExpressions(
        double canvasX, double canvasY,
        double canvasWidth, double canvasHeight,
        int renderWidth, int renderHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
            return ("(w-text_w)/2", "h*0.85-text_h/2");

        var x = (int)Math.Round(canvasX / canvasWidth * renderWidth);
        var y = (int)Math.Round(canvasY / canvasHeight * renderHeight);

        return (
            XExpr: x.ToString(CultureInfo.InvariantCulture),
            YExpr: y.ToString(CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// Scale a font size from canvas space to render space.
    /// </summary>
    public static int ScaleFontSize(double canvasFontSize, double canvasHeight, int renderHeight)
    {
        if (canvasHeight <= 0)
            return (int)canvasFontSize;

        return Math.Max(8, (int)Math.Round(canvasFontSize * renderHeight / canvasHeight));
    }

    /// <summary>
    /// Convert a hex color string (#RRGGBB or #AARRGGBB) to FFmpeg-compatible color format.
    /// </summary>
    public static string HexToFfmpegColor(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
            return "white";

        // Remove # prefix and convert to FFmpeg 0xRRGGBB format
        var hex = hexColor.TrimStart('#');
        if (hex.Length == 8) // AARRGGBB -> RRGGBB (drop alpha for simplicity)
            hex = hex[2..];

        return hex.Length == 6 ? $"0x{hex}" : "white";
    }
}
