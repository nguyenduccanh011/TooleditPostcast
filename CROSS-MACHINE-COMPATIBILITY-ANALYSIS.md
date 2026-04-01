# Cross-Machine Compatibility Analysis: Database Schema Mismatch

**Status:** Root cause identified — Database schema synchronization issue  
**Error:** `SQLite Error 1: 'no such column: t.TextStylesJson'`  
**Affected:** Projects fail to load on machine B while working perfectly on machine A

---

## Executive Summary

The application **CAN run normally on one machine but fail on another** due to **database schema synchronization problems**. This is a common issue in multi-machine deployments where:

1. **Machine A** has a fully migrated database with all recent schema changes (TextStyleJson column)
2. **Machine B** has a stale database with older schema missing recent migrations
3. EF Core attempts to query the new column that doesn't exist yet

---

## Root Cause Analysis

### Why This Happens

```
Machine A (Working)                    Machine B (Failing)
┌─────────────────────┐               ┌──────────────────┐
│ Database: app.db    │               │ Database: app.db │
├─────────────────────┤               ├──────────────────┤
│ Tracks table:       │               │ Tracks table:    │
│ ✅ TextStyleJson    │ (NEW)         │ ❌ TextStyleJson │ (MISSING)
│ ✅ Other columns    │               │ ✅ Other columns │
└─────────────────────┘               └──────────────────┘
         ↓                                     ↓
  Load Projects OK                    "no such column"
      ✅ Success                           ❌ Error
```

### The Query Chain

1. **ProjectViewModel.RefreshProjectsAsync()** → calls `_projectService.GetRecentProjectsAsync(10)`

2. **ProjectService.GetRecentProjectsAsync()** executes:
   ```csharp
   await context.Projects
       .AsNoTracking()
       .AsSplitQuery()
       .Include(p => p.Tracks)      // ← Loads Tracks with ALL properties
       .ThenInclude(t => t.Segments) // ← Then loads Segments
       .Include(p => p.Assets)
       .OrderByDescending(p => p.UpdatedAt)
       .Take(count)
       .ToListAsync();
   ```

3. **EF Core translates this to SQL:**
   ```sql
   SELECT t.Id, t.ProjectId, t.Name, t.TrackType, ..., t.TextStyleJson, ...
   FROM Tracks t WHERE ...
   ```

4. **SQLite executes the query** and **fails** because `TextStyleJson` column doesn't exist in the Tracks schema on Machine B

---

## Why the Migration Repair Isn't Working

The application includes a **DatabaseMigrationService** with repair logic:

```csharp
private static void RepairMigrationHistory(AppDbContext context)
{
    MarkMigrationIfColumnExists(conn,
        migrationId: "20260330100000_AddTrackTextStyleJson",
        checkSql: "SELECT COUNT(*) FROM pragma_table_info('Tracks') 
                   WHERE name='TextStyleJson'",
        productVersion);
}
```

**This repair logic has a gap:**
- It only marks migrations as applied **if the column already exists**
- It doesn't **CREATE** missing columns
- If the database is freshly copied without migrations running, the repair fails silently

**The EnsureColumnsExist() fallback only handles:**
```csharp
"ALTER TABLE Assets ADD COLUMN GlobalAssetId TEXT"
```

It does NOT include `TextStyleJson` for Tracks!

---

## Why This Differs Between Machines

### Machine A (Dev/Original)
```
Timeline:
1. Developer builds app → migrations applied locally to app.db
2. app.db created with full schema (including TextStyleJson)
3. app.db stored in AppData: C:\Users\...\AppData\Roaming\PodcastVideoEditor\
4. App runs fine ✅
```

### Machine B (New Install/Copy)
```
Timeline:
1. App installed fresh OR database copied from old build
2. app.db created with OLDER schema (before AddTrackTextStyleJson migration)
3. App starts → DatabaseMigrationService runs:
   - context.Database.Migrate() is called
   - But if __EFMigrationsHistory table already exists with old migrations,
     EF Core may skip new migrations if it thinks they're already applied (corrupted history)
   - OR if there's a conflict, the exception handler falls back to EnsureCreated()
     which SKIPS ALTER on existing tables
4. app.b missing TextStyleJson column ❌
5. Load Projects fails with "no such column" ❌
```

---

## Solution Pathways

### ✅ For Users (Immediate Fix)

**Option 1: Delete the stale database** (Simplest)
```powershell
# On Machine B:
$appDataPath = "$env:APPDATA\PodcastVideoEditor"
Remove-Item -Path "$appDataPath\app.db" -Force
```
Then restart the app. It will:
1. Create a fresh, empty database
2. Run all migrations cleanly
3. Load with full schema ✅

**Option 2: Manually add the missing column** (If you have data to preserve)
```sql
ALTER TABLE Tracks ADD COLUMN TextStyleJson TEXT;
```

---

### 🔧 For Developers (Permanent Fix)

**Update `DatabaseMigrationService.EnsureColumnsExist()` to include TextStyleJson:**

```csharp
private static void EnsureColumnsExist(AppDbContext context)
{
    var alterStatements = new[]
    {
        "ALTER TABLE Tracks ADD COLUMN TextStyleJson TEXT",     // ← ADD THIS
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
            catch (Microsoft.Data.Sqlite.SqliteException ex) 
                when (ex.Message.Contains("duplicate column"))
            {
                // Column already exists — expected
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
```

---

## Prevention for Future Migrations

Every time a new column is added to a database table:

1. **Create the migration:** `dotnet ef migrations add AddXyzColumn`
2. **Add to RepairMigrationHistory():**
   ```csharp
   MarkMigrationIfColumnExists(conn,
       migrationId: "YYYYMMDDHHMMSS_AddXyzColumn",
       checkSql: "SELECT COUNT(*) FROM pragma_table_info('TableName') WHERE name='ColumnName'",
       productVersion);
   ```
3. **Add to EnsureColumnsExist():**
   ```csharp
   "ALTER TABLE TableName ADD COLUMN ColumnName TYPE"
   ```

This ensures even stale/corrupted databases get repaired on startup.

---

## Verification Checklist

- [ ] **Machine A works:** Projects load, timeline visible
- [ ] **Machine B fails:** Error message includes "no such column"
- [ ] **Cause confirmed:** Check SQLite schema with DatabaseQuery tool
  ```csharp
  var conn = new SqliteConnection("Data Source=app.db");
  var cmd = conn.CreateCommand();
  cmd.CommandText = "PRAGMA table_info('Tracks')";
  // Check if TextStyleJson column exists
  ```

---

## Summary Table

| Aspect | Machine A | Machine B | Why Different |
|--------|-----------|-----------|---|
| App Executable | Same | Same | ✅ Identical |
| EF Core Version | 9.0.0 | 9.0.0 | ✅ Identical |
| Code | Same | Same | ✅ Identical |
| **Database File** | ✅ Has TextStyleJson | ❌ Missing TextStyleJson | ⚠️ **Schema mismatch** |
| Migration History | Clean | Corrupted/Stale | ⚠️ **Not synced** |
| **Result** | Loads projects ✅ | Error ❌ | Root cause |

---

## Conclusion

**Yes, it is absolutely possible** for an application to run on one machine but fail on another **even with identical code and build configuration**. 

**The culprit: Database schema state.** SQLite databases are local files that don't automatically stay in sync with code migrations. When a database schema doesn't match what the model expects, EF Core queries fail with cryptic column-not-found errors.

**The solution:** Ensure database migrations run completely on all machines, or delete stale databases to regenerate them cleanly.
