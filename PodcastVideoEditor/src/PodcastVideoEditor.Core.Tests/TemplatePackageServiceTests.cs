using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Services;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace PodcastVideoEditor.Core.Tests;

public class TemplatePackageServiceTests
{
    [Fact]
    public async Task ExportAndImportPackage_RoundTripsNestedMediaPaths()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"pve-template-tests-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(testRoot, "test.db");
        var packagePath = Path.Combine(testRoot, "exported.pvtemplate");
        var sourceMediaPath = Path.Combine(testRoot, "source-logo.png");

        Directory.CreateDirectory(testRoot);
        await File.WriteAllBytesAsync(sourceMediaPath, new byte[] { 1, 2, 3, 4, 5 });

        try
        {
            var factory = CreateFactory(dbPath);
            using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var service = new TemplatePackageService(factory, testRoot);

            var layoutJson = JsonSerializer.Serialize(new
            {
                renderSettings = new { aspectRatio = "9:16" },
                elements = new[]
                {
                    new
                    {
                        type = "Image",
                        propertiesJson = JsonSerializer.Serialize(new
                        {
                            filePath = sourceMediaPath,
                            caption = "Hero image"
                        })
                    },
                    new
                    {
                        type = "Logo",
                        propertiesJson = JsonSerializer.Serialize(new
                        {
                            imagePath = sourceMediaPath
                        })
                    }
                }
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await service.ExportTemplatePackageAsync(
                "Sample Template",
                "Template description",
                layoutJson,
                null,
                packagePath);

            Assert.True(File.Exists(packagePath));

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                Assert.NotNull(archive.GetEntry("manifest.json"));
                var layoutEntry = archive.GetEntry("layout.json");
                Assert.NotNull(layoutEntry);
                var assetEntries = archive.Entries.Where(entry => entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.Single(assetEntries);

                using var reader = new StreamReader(layoutEntry!.Open());
                var exportedLayout = await reader.ReadToEndAsync();
                Assert.StartsWith("{", exportedLayout, StringComparison.Ordinal);
                Assert.Contains("assets/", exportedLayout);
                Assert.DoesNotContain(sourceMediaPath, exportedLayout);

                using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
                var manifestJson = await manifestReader.ReadToEndAsync();
                Assert.DoesNotContain(sourceMediaPath, manifestJson);
            }

            var imported = await service.ImportTemplatePackageAsync(packagePath);

            Assert.Equal("Sample Template", imported.Name);
            Assert.Equal("Template description", imported.Description);

            using var importedLayout = JsonDocument.Parse(imported.LayoutJson);
            var elements = importedLayout.RootElement.GetProperty("elements");
            Assert.Equal(2, elements.GetArrayLength());

            foreach (var element in elements.EnumerateArray())
            {
                var propertiesJson = element.GetProperty("propertiesJson").GetString();
                Assert.False(string.IsNullOrWhiteSpace(propertiesJson));

                using var properties = JsonDocument.Parse(propertiesJson!);
                var pathProperty = properties.RootElement.TryGetProperty("filePath", out var filePathElement)
                    ? filePathElement.GetString()
                    : properties.RootElement.GetProperty("imagePath").GetString();

                Assert.False(string.IsNullOrWhiteSpace(pathProperty));
                Assert.StartsWith(testRoot, pathProperty!, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(pathProperty!));
            }

            using (var context = factory.CreateDbContext())
            {
                var template = await context.Templates.SingleAsync();
                Assert.Equal(imported.Id, template.Id);
                Assert.Equal(imported.Name, template.Name);
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                try
                {
                    Directory.Delete(testRoot, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite can hold the temp DB file open briefly on Windows.
                }
            }
        }
    }

    private static IDbContextFactory<AppDbContext> CreateFactory(string dbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new TestDbContextFactory(options);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
            => new AppDbContext(_options);
    }
}