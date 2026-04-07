#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

public class TemplatePackageService
{
    private const string ManifestFileName = "manifest.json";
    private const string LayoutFileName = "layout.json";
    private const string AssetFolderName = "assets";
    private const string PackageSchemaVersion = "1";

    private static readonly HashSet<string> PathPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FilePath",
        "ImagePath",
        "SourcePath",
        "FontFilePath"
    };

    private static readonly HashSet<string> NestedJsonStringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PropertiesJson",
        "TextStyleJson"
    };

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly string _templateStorageRoot;

    public TemplatePackageService(IDbContextFactory<AppDbContext> contextFactory, string? appDataPath = null)
    {
        _contextFactory = contextFactory;
        var basePath = string.IsNullOrWhiteSpace(appDataPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor")
            : appDataPath;

        _templateStorageRoot = Path.Combine(basePath, "templates");
    }

    public async Task ExportTemplatePackageAsync(
        string name,
        string description,
        string layoutJson,
        string? thumbnailBase64,
        string destinationPackagePath)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Template name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(destinationPackagePath))
            throw new ArgumentException("Destination path cannot be empty", nameof(destinationPackagePath));

        var layoutNode = ParseLayout(layoutJson);
        var assetMap = new Dictionary<string, TemplatePackageAssetEntry>(StringComparer.OrdinalIgnoreCase);
        RewriteOutgoingPaths(layoutNode, sourcePath => RegisterAsset(sourcePath, assetMap));

        var manifest = new TemplatePackageManifest
        {
            SchemaVersion = PackageSchemaVersion,
            Name = name,
            Description = description ?? string.Empty,
            ThumbnailBase64 = thumbnailBase64,
            CreatedAtUtc = DateTime.UtcNow,
            LayoutFile = LayoutFileName,
            Assets = assetMap.Values.OrderBy(a => a.PackagePath, StringComparer.OrdinalIgnoreCase).ToList()
        };

        // Validate all assets exist before attempting export
        var missingAssets = manifest.Assets
            .Where(a => !File.Exists(a.SourcePath))
            .Select(a => new { FileName = a.OriginalFileName ?? Path.GetFileName(a.SourcePath), Path = a.SourcePath })
            .ToList();

        if (missingAssets.Count > 0)
        {
            var missingList = string.Join("\n  - ", missingAssets.Take(10).Select(m => $"{m.FileName}"));
            var totalMissing = missingAssets.Count > 10 ? $"\n  ... and {missingAssets.Count - 10} more files" : string.Empty;
            throw new InvalidOperationException(
                $"Cannot export template: {missingAssets.Count} image file(s) not found.\n\n" +
                $"Missing files:\n  - {missingList}{totalMissing}\n\n" +
                $"Please verify these images exist before exporting.");
        }

        var directory = Path.GetDirectoryName(destinationPackagePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(destinationPackagePath))
            File.Delete(destinationPackagePath);

        using var packageStream = File.Create(destinationPackagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create);

        await WriteTextEntryAsync(archive, ManifestFileName, JsonSerializer.Serialize(manifest, JsonOptions));
        await WriteTextEntryAsync(archive, LayoutFileName, layoutNode?.ToJsonString(JsonOptions) ?? "{}");

        foreach (var asset in manifest.Assets)
        {

            var entry = archive.CreateEntry(asset.PackagePath, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(asset.SourcePath);
            await fileStream.CopyToAsync(entryStream);
        }

        Log.Information("Exported template package {PackagePath} with {AssetCount} asset(s)",
            destinationPackagePath, manifest.Assets.Count);
    }

    public async Task<Template> ImportTemplatePackageAsync(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path cannot be empty", nameof(packagePath));

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Template package not found", packagePath);

        Directory.CreateDirectory(_templateStorageRoot);

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestText = await ReadTextEntryAsync(archive, ManifestFileName);
        var layoutText = await ReadTextEntryAsync(archive, LayoutFileName);

        var manifest = JsonSerializer.Deserialize<TemplatePackageManifest>(manifestText, JsonOptions)
            ?? throw new InvalidDataException("Template package manifest is invalid.");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("Template package manifest does not contain a template name.");

        var templateId = Guid.NewGuid().ToString();
        var templateFolder = Path.Combine(_templateStorageRoot, templateId);
        Directory.CreateDirectory(templateFolder);

        var importedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets ?? [])
        {
            var entry = archive.GetEntry(asset.PackagePath)
                ?? throw new InvalidDataException($"Template package is missing asset entry: {asset.PackagePath}");

            var localPath = Path.Combine(templateFolder, asset.PackagePath.Replace('/', Path.DirectorySeparatorChar));
            var localDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(localDirectory))
                Directory.CreateDirectory(localDirectory);

            await using var entryStream = entry.Open();
            await using var outputStream = File.Create(localPath);
            await entryStream.CopyToAsync(outputStream);
            importedAssets[asset.PackagePath] = localPath;
        }

        var layoutNode = ParseLayout(layoutText);
        RewriteIncomingPaths(layoutNode, importedAssets);

        var template = new Template
        {
            Id = templateId,
            Name = manifest.Name,
            Description = manifest.Description ?? string.Empty,
            LayoutJson = layoutNode?.ToJsonString(JsonOptions) ?? "{}",
            ThumbnailBase64 = manifest.ThumbnailBase64,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = null,
            IsSystem = false
        };

        using var context = _contextFactory.CreateDbContext();
        context.Templates.Add(template);
        await context.SaveChangesAsync();

        Log.Information("Imported template package {PackagePath} as template {TemplateId}", packagePath, template.Id);
        return template;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static JsonNode? ParseLayout(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(layoutJson);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse template layout JSON; using empty layout");
            return new JsonObject();
        }
    }

    private static TemplatePackageAssetEntry RegisterAsset(string sourcePath, IDictionary<string, TemplatePackageAssetEntry> assetMap)
    {
        var normalizedPath = NormalizeFilePath(sourcePath);
        if (assetMap.TryGetValue(normalizedPath, out var existing))
            return existing;

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Template asset not found: {sourcePath}", sourcePath);

        var fileName = Path.GetFileName(sourcePath);
        var safeName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "asset";

        var extension = Path.GetExtension(fileName);
        var packageFileName = $"{safeName}_{Guid.NewGuid():N}{extension}";
        var packagePath = CombinePackagePath(AssetFolderName, packageFileName);

        var entry = new TemplatePackageAssetEntry
        {
            PackagePath = packagePath,
            SourcePath = sourcePath,
            OriginalFileName = fileName
        };

        assetMap[normalizedPath] = entry;
        return entry;
    }

    private static void RewriteOutgoingPaths(JsonNode? node, Func<string, TemplatePackageAssetEntry> registerAsset)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (NestedJsonStringKeys.Contains(property.Key) && TryGetString(property.Value, out var nestedJson) && !string.IsNullOrWhiteSpace(nestedJson))
                {
                    var nestedNode = ParseLayout(nestedJson);
                    RewriteOutgoingPaths(nestedNode, registerAsset);
                    obj[property.Key] = nestedNode?.ToJsonString(JsonOptions) ?? nestedJson;
                    continue;
                }

                if (PathPropertyKeys.Contains(property.Key) && TryGetString(property.Value, out var pathValue) && !string.IsNullOrWhiteSpace(pathValue))
                {
                    var asset = registerAsset(pathValue!);
                    obj[property.Key] = asset.PackagePath;
                    continue;
                }

                RewriteOutgoingPaths(property.Value, registerAsset);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RewriteOutgoingPaths(child, registerAsset);
        }
    }

    private static void RewriteIncomingPaths(JsonNode? node, IReadOnlyDictionary<string, string> packagePathMap)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (NestedJsonStringKeys.Contains(property.Key) && TryGetString(property.Value, out var nestedJson) && !string.IsNullOrWhiteSpace(nestedJson))
                {
                    var nestedNode = ParseLayout(nestedJson);
                    RewriteIncomingPaths(nestedNode, packagePathMap);
                    obj[property.Key] = nestedNode?.ToJsonString(JsonOptions) ?? nestedJson;
                    continue;
                }

                if (PathPropertyKeys.Contains(property.Key) && TryGetString(property.Value, out var pathValue) && !string.IsNullOrWhiteSpace(pathValue))
                {
                    if (packagePathMap.TryGetValue(pathValue!, out var localPath))
                        {
                            obj[property.Key] = localPath;
                        }
                        else
                        {
                            Log.Warning("Template import: Asset path not resolved: {PackagePath}. This path may fail during project creation.", pathValue);
                        }

                RewriteIncomingPaths(property.Value, packagePathMap);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RewriteIncomingPaths(child, packagePathMap);
        }
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    private static async Task<string> ReadTextEntryAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"Template package is missing required entry: {entryName}");

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static string CombinePackagePath(string folder, string fileName)
        => string.Join('/', folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Concat(new[] { fileName }));

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private sealed class TemplatePackageManifest
    {
        public string SchemaVersion { get; set; } = PackageSchemaVersion;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string LayoutFile { get; set; } = LayoutFileName;
        public string? ThumbnailBase64 { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<TemplatePackageAssetEntry> Assets { get; set; } = [];
    }

    private sealed class TemplatePackageAssetEntry
    {
        public string PackagePath { get; set; } = string.Empty;

        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

        [JsonIgnore]
        public string OriginalFileName { get; set; } = string.Empty;
    }
}