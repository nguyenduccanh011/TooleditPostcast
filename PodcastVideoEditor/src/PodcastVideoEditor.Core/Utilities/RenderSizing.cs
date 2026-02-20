using System.Globalization;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Shared sizing helpers so preview and render always use the same rules.
/// </summary>
public static class RenderSizing
{
    private static readonly int[] SupportedShortEdges = [480, 720, 1080];

    public static (int Width, int Height) ResolveRenderSize(string resolutionLabel, string aspectRatio)
    {
        var shortEdge = ParseResolutionLabel(resolutionLabel);
        return ResolveSizeByShortEdge(shortEdge, aspectRatio);
    }

    public static (double Width, double Height) ResolvePreviewSize(string aspectRatio, int previewShortEdge = 1080)
    {
        var (w, h) = ResolveSizeByShortEdge(previewShortEdge, aspectRatio);
        return (w, h);
    }

    public static string InferResolutionLabel(int width, int height)
    {
        var shortEdge = Math.Min(Math.Abs(width), Math.Abs(height));
        if (shortEdge <= 0)
            return "1080p";

        var nearest = SupportedShortEdges
            .OrderBy(v => Math.Abs(v - shortEdge))
            .First();

        return $"{nearest}p";
    }

    public static string NormalizeAspectRatio(string aspectRatio)
    {
        var (w, h) = ParseAspectRatio(aspectRatio);
        return $"{w.ToString(CultureInfo.InvariantCulture)}:{h.ToString(CultureInfo.InvariantCulture)}";
    }

    public static (int Width, int Height) EnsureEvenDimensions(int width, int height)
    {
        var normalizedWidth = Math.Max(2, width);
        var normalizedHeight = Math.Max(2, height);

        if (normalizedWidth % 2 != 0)
            normalizedWidth++;

        if (normalizedHeight % 2 != 0)
            normalizedHeight++;

        return (normalizedWidth, normalizedHeight);
    }

    private static (int Width, int Height) ResolveSizeByShortEdge(int shortEdge, string aspectRatio)
    {
        var (ratioW, ratioH) = ParseAspectRatio(aspectRatio);

        double width;
        double height;
        if (ratioW >= ratioH)
        {
            height = shortEdge;
            width = shortEdge * (ratioW / ratioH);
        }
        else
        {
            width = shortEdge;
            height = shortEdge * (ratioH / ratioW);
        }

        return EnsureEvenDimensions((int)Math.Round(width), (int)Math.Round(height));
    }

    private static (double Width, double Height) ParseAspectRatio(string aspectRatio)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return (9, 16);

        var parts = aspectRatio.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            w <= 0 ||
            h <= 0)
        {
            return (9, 16);
        }

        return (w, h);
    }

    private static int ParseResolutionLabel(string resolutionLabel)
    {
        if (string.IsNullOrWhiteSpace(resolutionLabel))
            return 1080;

        var text = resolutionLabel.Trim();
        if (text.EndsWith("p", StringComparison.OrdinalIgnoreCase))
            text = text[..^1];

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(2, parsed)
            : 1080;
    }
}
