#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.Services;
using System.IO;
using Xunit;

namespace PodcastVideoEditor.Ui.Tests;

public class DatabaseMigrationServiceTests
{
    [Fact]
    public void InitializeDatabase_RepairsStaleMigrationHistoryWithoutThrowing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"pve-migration-tests-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(testRoot, "app.db");
        Directory.CreateDirectory(testRoot);

        try
        {
            var factory = CreateFactory(dbPath);

            using (var context = factory.CreateDbContext())
            {
                context.Database.Migrate();
                context.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsHistory;");
            }

            using (var context = factory.CreateDbContext())
            {
                DatabaseMigrationService.InitializeDatabase(context);

                var migrationIds = ReadMigrationIds(context);
                Assert.Contains("20260206163048_InitialCreate", migrationIds);
                Assert.Contains("20260212034910_AddMultiTrackSupport", migrationIds);
                Assert.Contains("20260406040516_AddTrackRoleAndSpanMode", migrationIds);
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
                    // SQLite can keep the file locked briefly on Windows.
                }
            }
        }
    }

    [Fact]
    public void InitializeDatabase_NormalizesLegacyTitleOverlayTracks()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"pve-migration-title-tests-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(testRoot, "app.db");
        Directory.CreateDirectory(testRoot);

        try
        {
            var factory = CreateFactory(dbPath);

            using (var context = factory.CreateDbContext())
            {
                context.Database.Migrate();
                context.Projects.Add(new Project
                {
                    Name = "Legacy title project",
                    RenderSettings = new RenderSettings(),
                    Tracks =
                    [
                        new Track
                        {
                            TrackType = TrackTypes.Text,
                            TrackRole = TrackRoles.TitleOverlay,
                            SpanMode = TrackSpanModes.SegmentBound,
                            Name = "Title 1",
                            IsVisible = true
                        }
                    ]
                });
                context.SaveChanges();
            }

            using (var context = factory.CreateDbContext())
            {
                DatabaseMigrationService.InitializeDatabase(context);
            }

            using (var context = factory.CreateDbContext())
            {
                var track = context.Tracks.Single();
                Assert.Equal(TrackRoles.Unspecified, track.TrackRole);
                Assert.Equal(TrackSpanModes.ProjectDuration, track.SpanMode);
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
                    // SQLite can keep the file locked briefly on Windows.
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

    private static List<string> ReadMigrationIds(AppDbContext context)
    {
        var migrationIds = new List<string>();
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                migrationIds.Add(reader.GetString(0));
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }

        return migrationIds;
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
