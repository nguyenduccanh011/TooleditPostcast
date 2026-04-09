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
            EnsureColumnsExist(context);
            return;
        }
        catch (Exception ex) when (IsMigrationConflict(ex))
        {
            Log.Warning(ex, "Database migration failed. Repairing migration history and retrying...");
        }

        RepairMigrationHistory(context);
        EnsureColumnsExist(context);
        try
        {
            context.Database.Migrate();
            Log.Information("Database migration applied successfully after history repair");
            EnsureColumnsExist(context);
        }
        catch (Exception ex) when (IsMigrationConflict(ex))
        {
            RepairMigrationHistory(context);
            EnsureColumnsExist(context);
            context.Database.Migrate();
            Log.Information("Database migration applied successfully after second history repair");
            EnsureColumnsExist(context);
        }
    }

    private static bool IsMigrationConflict(Exception ex)
        => ex is Microsoft.Data.Sqlite.SqliteException sqliteEx
            && (sqliteEx.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
                || sqliteEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    private static void RepairMigrationHistory(AppDbContext context)
    {
        const string productVersion = "8.0.2";
        var conn = context.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            EnsureMigrationHistoryTable(conn);

            MarkMigrationIfAllTablesExist(conn,
                migrationId: "20260206163048_InitialCreate",
                productVersion,
                ["Projects", "Templates", "Assets", "BgmTracks", "Elements", "Segments"]);

            MarkMigrationIfTableAndColumnExist(conn,
                migrationId: "20260212034910_AddMultiTrackSupport",
                tableName: "Tracks",
                columnTableName: "Segments",
                columnName: "TrackId",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260212120000_AddElementSegmentId",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Elements') WHERE name='SegmentId'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260210100000_AddSegmentKind",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Kind'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260313100000_AddSegmentAudioProperties",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='FadeOutDuration'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260318100000_AddKeywordsToSegment",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Keywords'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260318120000_AddTrackImageLayoutPreset",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='ImageLayoutPreset'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260320100000_AddSegmentSourceStartOffset",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='SourceStartOffset'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260326100000_AddMotionEffectProperties",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='MotionPreset'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260328100000_AddOverlayProperties",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='OverlayColorHex'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260330100000_AddTrackTextStyleJson",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='TextStyleJson'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260331100000_AddGlobalAssetId",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Assets') WHERE name='GlobalAssetId'",
                productVersion);

            MarkMigrationIfColumnExists(conn,
                migrationId: "20260406040516_AddTrackRoleAndSpanMode",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='SpanMode'",
                productVersion);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

    private static void EnsureMigrationHistoryTable(System.Data.Common.DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
                ProductVersion TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
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

    private static void MarkMigrationIfAllTablesExist(
        System.Data.Common.DbConnection conn,
        string migrationId,
        string productVersion,
        string[] tableNames)
    {
        foreach (var tableName in tableNames)
        {
            if (!TableExists(conn, tableName))
                return;
        }

        InsertMigrationHistoryRow(conn, migrationId, productVersion);
    }

    private static void MarkMigrationIfTableAndColumnExist(
        System.Data.Common.DbConnection conn,
        string migrationId,
        string tableName,
        string columnTableName,
        string columnName,
        string productVersion)
    {
        if (!TableExists(conn, tableName))
            return;

        var count = GetColumnCount(conn, columnTableName, columnName);
        if (count > 0)
            InsertMigrationHistoryRow(conn, migrationId, productVersion);
    }

    private static long GetColumnCount(System.Data.Common.DbConnection conn, string tableName, string columnName)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name='{columnName}'";
        return (long)(checkCmd.ExecuteScalar() ?? 0L);
    }

    private static bool TableExists(System.Data.Common.DbConnection conn, string tableName)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        var parameter = checkCmd.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = tableName;
        checkCmd.Parameters.Add(parameter);
        return Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static void InsertMigrationHistoryRow(System.Data.Common.DbConnection conn, string migrationId, string productVersion)
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

    /// <summary>
    /// Directly ALTER TABLE to add missing columns. Safe to call repeatedly —
    /// SQLite will throw "duplicate column" if already present, which we ignore.
    /// This is the last-resort guarantee that the schema matches the model.
    /// Handles all critical columns added in migrations to ensure stale/corrupted
    /// databases from other machines or old installations get repaired on startup.
    /// </summary>
    private static void EnsureColumnsExist(AppDbContext context)
    {
        var alterStatements = new[]
        {
            // Tracks table columns
            "ALTER TABLE Tracks ADD COLUMN TextStyleJson TEXT",
            "ALTER TABLE Tracks ADD COLUMN TrackRole TEXT NOT NULL DEFAULT 'unspecified'",
            "ALTER TABLE Tracks ADD COLUMN SpanMode TEXT NOT NULL DEFAULT 'segment_bound'",
            
            // Assets table columns
            "ALTER TABLE Assets ADD COLUMN GlobalAssetId TEXT",
        };

        var conn = context.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            foreach (var sql in alterStatements)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                    Log.Information("Schema fix applied: {Sql}", sql);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
                {
                    // Column already exists — expected, nothing to do.
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Schema fix failed (non-critical): {Sql}", sql);
                }
            }
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
