using System.Net.Http;
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Downloads and prepares remote images before they are registered as project assets.
/// Keeps enough detail for 1080p/portrait crop workflows without storing oversized originals.
/// </summary>
public sealed class ImageAssetIngestService
{
    private const int DefaultMaxLongEdge = 2048;
    private const int JpegQuality = 90;

    private static readonly HttpClient s_sharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly HttpClient _http;
    private readonly int _maxLongEdge;

    public ImageAssetIngestService(HttpClient? httpClient = null, int maxLongEdge = DefaultMaxLongEdge)
    {
        _http = httpClient ?? s_sharedHttp;
        _maxLongEdge = Math.Max(512, maxLongEdge);
    }

    public async Task<PreparedImageAsset> DownloadAndPrepareAsync(
        string url,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Image URL cannot be empty", nameof(url));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var extension = GetPreferredExtension(response.Content.Headers.ContentType?.MediaType, url);
        var tempSource = Path.Combine(Path.GetTempPath(), $"pve_{SanitizeFileToken(logicalName)}_{Guid.NewGuid():N}{extension}");

        await using (var destination = File.Create(tempSource))
        {
            await response.Content.CopyToAsync(destination, cancellationToken);
        }

        try
        {
            return await PrepareImageFileAsync(tempSource, logicalName, deleteSourceWhenReencoded: true, cancellationToken);
        }
        catch
        {
            TryDeleteFile(tempSource);
            throw;
        }
    }

    public Task<PreparedImageAsset> PrepareImageFileAsync(
        string sourcePath,
        string logicalName,
        CancellationToken cancellationToken = default)
        => PrepareImageFileAsync(sourcePath, logicalName, deleteSourceWhenReencoded: false, cancellationToken);

    public static (int Width, int Height) ComputeNormalizedSize(int width, int height, int maxLongEdge)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive");

        var longEdge = Math.Max(width, height);
        if (longEdge <= maxLongEdge)
            return (width, height);

        var scale = maxLongEdge / (double)longEdge;
        var normalizedWidth = Math.Max(1, (int)Math.Round(width * scale));
        var normalizedHeight = Math.Max(1, (int)Math.Round(height * scale));
        return (normalizedWidth, normalizedHeight);
    }

    private async Task<PreparedImageAsset> PrepareImageFileAsync(
        string sourcePath,
        string logicalName,
        bool deleteSourceWhenReencoded,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Image file not found", sourcePath);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var codec = SKCodec.Create(sourcePath) ?? throw new InvalidOperationException("Unsupported image format");
            var encodedInfo = codec.Info;
            var originalWidth = encodedInfo.Width;
            var originalHeight = encodedInfo.Height;
            var (targetWidth, targetHeight) = ComputeNormalizedSize(originalWidth, originalHeight, _maxLongEdge);
            var sourceInfo = new SKImageInfo(originalWidth, originalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(sourceInfo);
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                throw new InvalidOperationException($"Could not decode image: {result}");

            var hasAlpha = encodedInfo.AlphaType != SKAlphaType.Opaque;
            var outputPath = sourcePath;

            if (targetWidth != originalWidth || targetHeight != originalHeight)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
                if (resized == null)
                    throw new InvalidOperationException("Could not resize image");

                var extension = hasAlpha ? ".png" : ".jpg";
                outputPath = Path.Combine(Path.GetTempPath(), $"pve_norm_{SanitizeFileToken(logicalName)}_{Guid.NewGuid():N}{extension}");

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, JpegQuality);
                using var fileStream = File.Create(outputPath);
                data.SaveTo(fileStream);

                if (deleteSourceWhenReencoded)
                    TryDeleteFile(sourcePath);
            }

            var outputInfo = new FileInfo(outputPath);
            return new PreparedImageAsset(outputPath, targetWidth, targetHeight, outputInfo.Length);
        }, cancellationToken);
    }

    private static string GetPreferredExtension(string? mediaType, string url)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
        }

        var extension = Path.GetExtension(url);
        return string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;
    }

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "image";

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Replace(':', '_').Replace('/', '_').Replace('\\', '_');
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

public sealed record PreparedImageAsset(string FilePath, int Width, int Height, long FileSize);