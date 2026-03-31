#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Manages the global asset library (built-in + user-imported).
/// Assets in this library are shared across all projects.
/// </summary>
public sealed class GlobalAssetService
{
    private const int MaxLongEdge = 2048;
    private const int JpegQuality = 90;

    private readonly IDbContextFactory<LibraryDbContext> _contextFactory;
    private readonly string _userLibraryPath;
    private readonly string _builtInLibraryPath;

    /// <summary>
    /// Known asset categories.
    /// </summary>
    public static readonly string[] Categories = ["Logo", "Icon", "Overlay", "Border", "Uncategorized"];

    public GlobalAssetService(
        IDbContextFactory<LibraryDbContext> contextFactory,
        string appDataPath,
        string installDir)
    {
        _contextFactory = contextFactory;
        _userLibraryPath = Path.Combine(appDataPath, "library");
        _builtInLibraryPath = Path.Combine(installDir, "library");
    }

    /// <summary>
    /// Ensure library.db schema exists (creates tables if needed).
    /// </summary>
    public void InitializeDatabase()
    {
        using var ctx = _contextFactory.CreateDbContext();
        ctx.Database.EnsureCreated();
        Log.Information("Library database initialized at {Path}", ctx.Database.GetConnectionString());
    }

    /// <summary>
    /// Scan the built-in library folder and register any new assets (by filename).
    /// Existing built-in assets (matching FileName) are skipped.
    /// </summary>
    public async Task ScanBuiltInAssetsAsync()
    {
        if (!Directory.Exists(_builtInLibraryPath))
        {
            Log.Debug("Built-in library folder not found: {Path}", _builtInLibraryPath);
            return;
        }

        using var ctx = _contextFactory.CreateDbContext();
        var existingFileNames = await ctx.GlobalAssets
            .Where(a => a.IsBuiltIn)
            .Select(a => a.FileName)
            .ToHashSetAsync();

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".svg" };

        var added = 0;

        foreach (var categoryDir in Directory.GetDirectories(_builtInLibraryPath))
        {
            var category = MapFolderToCategory(Path.GetFileName(categoryDir));

            foreach (var filePath in Directory.GetFiles(categoryDir))
            {
                var ext = Path.GetExtension(filePath);
                if (!imageExtensions.Contains(ext))
                    continue;

                var fileName = Path.GetFileName(filePath);
                if (existingFileNames.Contains(fileName))
                    continue;

                var (width, height) = ProbeImageDimensions(filePath);

                var asset = new GlobalAsset
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    Category = category,
                    FilePath = filePath,
                    FileName = fileName,
                    Extension = ext,
                    FileSize = new FileInfo(filePath).Length,
                    Width = width,
                    Height = height,
                    IsBuiltIn = true,
                    CreatedAt = DateTime.UtcNow
                };

                ctx.GlobalAssets.Add(asset);
                existingFileNames.Add(fileName);
                added++;
            }
        }

        if (added > 0)
        {
            await ctx.SaveChangesAsync();
            Log.Information("Registered {Count} new built-in library assets", added);
        }
    }

    /// <summary>
    /// Import a local image file into the user's global library.
    /// The file is copied and normalized (max 2048px long edge).
    /// </summary>
    public async Task<GlobalAsset> ImportAsync(
        string sourceFilePath,
        string category = "Uncategorized",
        string? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            throw new ArgumentException("File path cannot be empty", nameof(sourceFilePath));
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Source file not found", sourceFilePath);

        var categoryFolder = Path.Combine(_userLibraryPath, SanitizeFolderName(category));
        Directory.CreateDirectory(categoryFolder);

        var fileInfo = new FileInfo(sourceFilePath);
        var safeName = Path.GetFileNameWithoutExtension(fileInfo.Name);
        var destFileName = $"{SanitizeFolderName(safeName)}_{Guid.NewGuid():N}";

        // Normalize image (resize + re-encode)
        var (destPath, width, height) = await NormalizeImageAsync(
            sourceFilePath, categoryFolder, destFileName, ct);

        var destInfo = new FileInfo(destPath);

        var asset = new GlobalAsset
        {
            Name = Path.GetFileNameWithoutExtension(fileInfo.Name),
            Category = category,
            FilePath = destPath,
            FileName = destInfo.Name,
            Extension = destInfo.Extension,
            FileSize = destInfo.Length,
            Width = width,
            Height = height,
            IsBuiltIn = false,
            Tags = tags,
            CreatedAt = DateTime.UtcNow
        };

        using var ctx = _contextFactory.CreateDbContext();
        ctx.GlobalAssets.Add(asset);
        await ctx.SaveChangesAsync(ct);

        Log.Information("Global asset imported: {Name} ({Category})", asset.Name, asset.Category);
        return asset;
    }

    /// <summary>
    /// Get all global assets, optionally filtered by category.
    /// </summary>
    public async Task<List<GlobalAsset>> GetAssetsAsync(string? category = null, string? searchText = null)
    {
        using var ctx = _contextFactory.CreateDbContext();
        IQueryable<GlobalAsset> query = ctx.GlobalAssets.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category) && category != "All")
            query = query.Where(a => a.Category == category);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.ToLowerInvariant();
            query = query.Where(a =>
                a.Name.ToLower().Contains(term) ||
                (a.Tags != null && a.Tags.ToLower().Contains(term)));
        }

        return await query.OrderBy(a => a.IsBuiltIn ? 0 : 1)
                          .ThenBy(a => a.Name)
                          .ToListAsync();
    }

    /// <summary>
    /// Get all distinct categories that have at least one asset.
    /// </summary>
    public async Task<List<string>> GetCategoriesAsync()
    {
        using var ctx = _contextFactory.CreateDbContext();
        return await ctx.GlobalAssets
            .Select(a => a.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    /// <summary>
    /// Delete a user-imported global asset.
    /// Built-in assets cannot be deleted.
    /// </summary>
    public async Task<bool> DeleteAsync(string assetId)
    {
        using var ctx = _contextFactory.CreateDbContext();
        var asset = await ctx.GlobalAssets.FindAsync(assetId);
        if (asset == null)
            return false;

        if (asset.IsBuiltIn)
        {
            Log.Warning("Cannot delete built-in asset {Id}", assetId);
            return false;
        }

        // Delete file from disk
        if (File.Exists(asset.FilePath))
        {
            try { File.Delete(asset.FilePath); }
            catch (Exception ex) { Log.Warning(ex, "Failed to delete library file {Path}", asset.FilePath); }
        }

        ctx.GlobalAssets.Remove(asset);
        await ctx.SaveChangesAsync();
        Log.Information("Global asset deleted: {Id} ({Name})", assetId, asset.Name);
        return true;
    }

    /// <summary>
    /// Update category and/or tags for a global asset.
    /// </summary>
    public async Task<bool> UpdateAsync(string assetId, string? newCategory = null, string? newTags = null)
    {
        using var ctx = _contextFactory.CreateDbContext();
        var asset = await ctx.GlobalAssets.FindAsync(assetId);
        if (asset == null)
            return false;

        if (newCategory != null)
            asset.Category = newCategory;
        if (newTags != null)
            asset.Tags = newTags;

        await ctx.SaveChangesAsync();
        return true;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string MapFolderToCategory(string folderName)
    {
        return folderName.ToLowerInvariant() switch
        {
            "logos" or "logo" => "Logo",
            "icons" or "icon" => "Icon",
            "overlays" or "overlay" => "Overlay",
            "borders" or "border" => "Border",
            _ => "Uncategorized"
        };
    }

    private static (int Width, int Height) ProbeImageDimensions(string filePath)
    {
        try
        {
            using var codec = SKCodec.Create(filePath);
            if (codec != null)
                return (codec.Info.Width, codec.Info.Height);
        }
        catch { /* best effort */ }
        return (0, 0);
    }

    private static async Task<(string DestPath, int Width, int Height)> NormalizeImageAsync(
        string sourcePath,
        string destFolder,
        string destBaseName,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            using var codec = SKCodec.Create(sourcePath)
                ?? throw new InvalidOperationException("Unsupported image format");

            var info = codec.Info;
            var (tw, th) = ImageAssetIngestService.ComputeNormalizedSize(info.Width, info.Height, MaxLongEdge);
            var hasAlpha = info.AlphaType != SKAlphaType.Opaque;

            var sourceInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(sourceInfo);
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                throw new InvalidOperationException($"Could not decode image: {result}");

            SKBitmap? outputBitmap = null;
            try
            {
                if (tw != info.Width || th != info.Height)
                {
                    outputBitmap = bitmap.Resize(
                        new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul),
                        SKFilterQuality.High);
                    if (outputBitmap == null)
                        throw new InvalidOperationException("Could not resize image");
                }
                else
                {
                    outputBitmap = bitmap;
                }

                var extension = hasAlpha ? ".png" : ".jpg";
                var destPath = Path.Combine(destFolder, destBaseName + extension);

                using var image = SKImage.FromBitmap(outputBitmap);
                using var data = image.Encode(
                    hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                    JpegQuality);
                using var fs = File.Create(destPath);
                data.SaveTo(fs);

                return (destPath, tw, th);
            }
            finally
            {
                if (outputBitmap != null && outputBitmap != bitmap)
                    outputBitmap.Dispose();
            }
        }, ct);
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "asset" : sanitized;
    }
}
