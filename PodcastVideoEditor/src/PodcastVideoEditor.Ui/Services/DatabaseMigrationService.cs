#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using Serilog;
using System;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Applies EF Core migrations with automatic repair for stale migration history.
/// Extracted from MainWindow.xaml.cs to keep UI code-behind thin.
/// </summary>
internal static class DatabaseMigrationService
{
    public static void InitializeDatabase(AppDbContext context)
    {
        try
        {
            context.Database.Migrate();
            Log.Information("Database migration applied successfully");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column name"))
        {
            Log.Warning(ex, "Migration conflict detected (column already exists in schema). Repairing migration history...");
            RepairMigrationHistory(context);
            context.Database.Migrate();
            Log.Information("Database migration applied successfully after history repair");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database migration failed, falling back to EnsureCreated");
            context.Database.EnsureCreated();
        }
    }

    private static void RepairMigrationHistory(AppDbContext context)
    {
        const string productVersion = "8.0.2";
        var conn = context.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            MarkMigrationIfColumnExists(conn,
                migrationId: "20260210100000_AddSegmentKind",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Kind'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260313100000_AddSegmentAudioProperties",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Volume'",
                productVersion);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

    private static void MarkMigrationIfColumnExists(
        System.Data.Common.DbConnection conn,
        string migrationId,
        string checkSql,
        string productVersion)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = checkSql;
        var count = (long)(checkCmd.ExecuteScalar() ?? 0L);
        if (count > 0)
        {
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText =
                "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, @ver)";
            var pId = insertCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = migrationId;
            var pVer = insertCmd.CreateParameter(); pVer.ParameterName = "@ver"; pVer.Value = productVersion;
            insertCmd.Parameters.Add(pId);
            insertCmd.Parameters.Add(pVer);
            insertCmd.ExecuteNonQuery();
            Log.Information("Migration history repaired: marked {MigrationId} as applied", migrationId);
        }
    }
}
