# Database Persistence & Commercial Application Standards

## 📍 Vị Trí Database Hiện Tại

```
Windows Path:
C:\Users\{UserName}\AppData\Roaming\PodcastVideoEditor\app.db

Code Reference:
Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
  → C:\Users\{UserName}\AppData\Roaming  (Windows)
  → /Users/{UserName}/Library/Application Support  (macOS)
  → ~/.local/share  (Linux)
```

**Ví dụ cụ thể:**
```
C:\Users\DELL\AppData\Roaming\PodcastVideoEditor\app.db
```

### Cấu Trúc AppData Folder

```
C:\Users\DELL\AppData\
├── Roaming\
│   └── PodcastVideoEditor\
│       ├── app.db                    ← Database chính (Projects, Tracks, Segments)
│       ├── library.db                ← Global Asset Library
│       ├── logs\
│       │   └── app-{date}.log
│       └── assets\
│           └── {projectId}\
│               └── {assetId}.jpg, .mp3, etc.
├── Local\                            ← (unused hiện tại)
└── LocalLow\                         ← (unused hiện tại)
```

---

## ⚠️ Vấn Đề với Database Persistence Model Hiện Tại

### ❌ Các Điểm Yếu

| Vấn đề | Ảnh Hưởng | Nghiêm Trọng |
|--------|----------|------------|
| **Database nằm local** | Không share giữa máy → khó sync | 🔴 Cao |
| **Không có phiên bản quản lý** | Versions cũ bị ghi đè, không backup | 🔴 Cao |
| **Schema migrate tự động** | Có thể fail silently nếu database hỏng | 🟡 Trung |
| **Chỉ 1 database per máy** | Không hỗ trợ multi-user / team collaboration | 🔴 Cao |
| **AppData path có thể thay đổi** | Khó migrate nếu đổi Windows version | 🟡 Trung |

### ✅ Những Gì Làm Tốt

| Điểm Mạnh | Lợi Ích |
|----------|--------|
| **Isolated per user** | Không ảnh hưởng user khác trên same machine |
| **Self-contained SQLite** | Không cần server/admin privileges |
| **Auto-migrations on startup** | App tự fix schema nếu outdated |
| **Logs locally stored** | Debugging dễ hơn |

---

## 🏢 Cách Các Ứng Dụng Thương Mại Xử Lý (Enterprise Patterns)

### **1. Adobe Premiere Pro / DaVinci Resolve (Local-First)**

```
Architecture: Hybrid Local + Cloud Backup

📁 Local Database:
   C:\ProgramData\Adobe\Premiere\projects.db
   - Lưu project metadata, timeline state
   - Full schema migrations bundled in installer
   - Pre-check: verify schema before load

☁️ Cloud Backup:
   - Adobe Creative Cloud stores metadata backups
   - Version history (keep last 10 versions)
   - Full restore capability
   - Automatic sync on save

🔄 Migration Strategy:
   - Version hard-coded in app:             App Version 2024.1
   - Database schema version tracked:       Version 12
   - On startup:
     1. Check app version mismatch with DB version
     2. If mismatch:
        - Create backup: projects.db -> projects.db.backup.2024-03-15
        - Run automated migration script
        - If migration fails: show error + restore from backup
     3. If migration success: update schema version marker
```

### **2. Figma / Sketch (Web-First + Local Cache)**

```
Architecture: Cloud Primary + Local Cache

📁 Local Cache:
   ~/.figma-cache/
   ├── workspace-{id}.db      (local SQLite cache)
   ├── file-{id}.db
   └── metadata.json

☁️ Primary Storage:
   - All data on Figma servers
   - Local DB is "expendable" (can be cleared anytime)
   - Version always comes from cloud

🔄 Migration Strategy:
   - No complex migrations needed (cloud handles versioning)
   - If local DB corrupted: delete cache → re-sync from cloud
   - Version conflicts resolved by: "cloud is source of truth"
   - No user data loss (backup on server)
```

### **3. Microsoft Office / Visual Studio (Hybrid)**

```
Architecture: Hybrid with Version Tracking

📁 Local Storage:
   - Recent documents: HKEY_CURRENT_USER\Software\Microsoft\Office
   - User settings: separate from data
   - Backup folder: C:\Users\{User}\OneDrive\Backup (if enabled)

📋 Version Management:
   - Installer ships migrations for: Office 2019 → 2021 → 2024
   - Each version knows how to upgrade from previous N versions
   - On upgrade: run targeted migration scripts
   - Rollback: keep old format reader for 1-2 versions

🔄 Migration Strategy:
   - "Forward-compatible reader" approach:
     - New version CAN read old format (with warning)
     - Old version CANNOT read new format (prevents corruption)
   - Database version in: app metadata file (app.config)
   - Logs all migrations: %LocalAppData%\Microsoft\Office\Logs
   - Auto-backup before migration (if enabled in settings)
```

### **4. Blender (Self-Contained + Version Lock)**

```
Architecture: Simple but Robust

📁 File Storage:
   ~/.config/blender/{version}/
   ├── startup.blend           (scene template)
   ├── recent-files.txt
   └── userpref.blend          (settings)

📊 Database (None actually - uses file-based):
   - Each .blend file is self-contained
   - No external database management
   - Recent files tracked in plain text

🔄 Migration Strategy:
   - Version-aware on startup:
     - Blender 3.6 opens .blend from 3.5? Yes (backward compatible)
     - Blender 3.5 opens .blend from 4.0? No (shows error)
   - User can choose:
     - Save as old version (downgrade)
     - Upgrade and keep new version
   - No database conflicts possible
```

---

## 📊 Comparison Table: PodcastVideoEditor vs Commercial Standards

| Aspect | Current App | Adobe Premiere | Microsoft Office | Blender | Best Practice |
|--------|---------|-----------------|------------------|---------|---|
| **Database Location** | Local AppData | Local + Cloud | Local + Cloud | File-based | Varies (see below) |
| **Schema Versioning** | ❌ None tracked | ✅ Version tracked | ✅ Version tracked | ✅ File format versioned | ✅ Required |
| **Migration on Upgrade** | ⚠️ Auto-attempted | ✅ Backup then migrate | ✅ Backup then migrate | ✅ File conversion | ✅ Required |
| **Backup Before Migration** | ❌ None | ✅ Auto | ✅ Auto | ✅ Not needed | ✅ Required |
| **Multi-Version Support** | ❌ Current only | ✅ Last 3 versions | ✅ Varies | ✅ Current + 1 back | ✅ 2-3 versions |
| **Rollback Capability** | ❌ None | ✅ From backup | ✅ From backup | ❌ Convert back | ✅ Required |
| **Cloud Sync** | ❌ None | ✅ Optional | ✅ Optional | ❌ Manual | Depends on use case |
| **Data Loss Risk** | 🔴 High | 🟢 Low | 🟢 Low | 🟢 Low | 🟢 Low |

---

## 🎯 Enterprise-Grade Implementation for PodcastVideoEditor

### **Recommended Architecture**

```
Tier 1: Local SQLite (Current)
├── app.db (main database)
├── app.db.backup.{timestamp}  ← Created before each migration
└── .schema-version (contains: "20260401-v3")

Tier 2: Version Tracking
├── Store current schema version in database itself
├── Compare app code version with DB version on startup
└── If mismatch: trigger migration + backup

Tier 3: Migration Safety
├── Always create backup before migration
├── Log migration attempts with timestamp
├── Rollback on failure: restore from .backup file
└── Show user: "Database upgraded, previous saved at {path}"

Tier 4: Cloud Sync (Future - Optional)
├── Export projects to OneDrive / Google Drive
├── Version control: Git-like project history
└── Restore from cloud if local corrupted
```

### **Step-by-Step Implementation**

#### **Step 1: Add Schema Version Tracking**

```csharp
// In DatabaseMigrationService.cs
private const string AppSchemaVersion = "20260401"; // Update with each migration

public static void InitializeDatabase(AppDbContext context)
{
    try
    {
        // 1. Check version mismatch
        var currentDbVersion = GetDatabaseSchemaVersion(context);
        if (currentDbVersion == null)
        {
            Log.Information("First run - initializing fresh database");
            context.Database.Migrate();
            StoreDatabaseSchemaVersion(context, AppSchemaVersion);
        }
        else if (currentDbVersion != AppSchemaVersion)
        {
            Log.Warning("Schema version mismatch: DB={DbVersion}, App={AppVersion}",
                currentDbVersion, AppSchemaVersion);
            
            // 2. Create backup BEFORE migration
            CreateDatabaseBackup(context);
            
            // 3. Run migrations
            context.Database.Migrate();
            
            // 4. Update version marker
            StoreDatabaseSchemaVersion(context, AppSchemaVersion);
            
            Log.Information("Database upgraded successfully");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Migration failed - attempting rollback");
        RestoreDatabaseFromBackup();
        throw;
    }
}

private static string? GetDatabaseSchemaVersion(AppDbContext context)
{
    // Store version in a simple metadata table or text file
    var conn = context.Database.GetDbConnection();
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Value FROM __AppMetadata WHERE Key = 'SchemaVersion' LIMIT 1";
    var result = cmd.ExecuteScalar()?.ToString();
    conn.Close();
    return result;
}

private static void CreateDatabaseBackup(AppDbContext context)
{
    var dbPath = context.Database.GetDbConnection().ConnectionString
        .Split("=")[1].TrimEnd(';');
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var backupPath = $"{dbPath}.backup.{timestamp}";
    File.Copy(dbPath, backupPath, overwrite: false);
    Log.Information("Database backed up to: {BackupPath}", backupPath);
}

private static void RestoreDatabaseFromBackup()
{
    var backupPath = Directory.GetFiles(
        Path.GetDirectoryName(dbPath),
        "app.db.backup.*",
        SearchOption.TopDirectoryOnly
    ).OrderByDescending(f => f).FirstOrDefault();
    
    if (backupPath != null)
    {
        File.Copy(backupPath, dbPath, overwrite: true);
        Log.Information("Database restored from: {BackupPath}", backupPath);
    }
}
```

#### **Step 2: Create Migration Deployment Checklist**

```yaml
Before Release:
  ☐ Create new migration file: dotnet ef migrations add {FeatureName}
  ☐ Test migration on fresh database
  ☐ Test migration on old database (backward compatibility)
  ☐ Update AppSchemaVersion constant in DatabaseMigrationService
  ☐ Update EnsureColumnsExist() with new columns
  ☐ Write migration notes in CHANGELOG.md
  ☐ Test rollback: can app still run with old schema?

Release Notes:
  ✅ Schema version: 20260401
  ✅ Database auto-migration: Yes (backup created)
  ✅ Breaking changes: None
  ✅ Rollback path: Restore from backup
```

#### **Step 3: User-Facing Error Messages**

```csharp
// Instead of silent failure, inform user
if (migrationFailed)
{
    ShowDialog(
        title: "Database Update Required",
        message: $"""
            Your project database needs to be updated.
            
            ✅ A backup has been created:
               {backupPath}
            
            🔄 Updating database schema...
            
            ⏱️ This may take 1-5 seconds.
            """,
        buttons: new[] { "OK" },
        icon: MessageBoxImage.Information
    );
}
```

---

## 🚀 Phân Loại: Commercial vs Current State

### **Current PodcastVideoEditor**

```
Category: DESKTOP APPLICATION (Single-User, Local-First)
Maturity: MVP / Early Stage
Database Strategy: Simplified (suitable for dev/beta)

✅ Acceptable for:
   - Single user per machine
   - Internal beta testing
   - Non-critical data
   - Development phase

❌ NOT suitable for:
   - Production commercial release
   - Multi-user collaborative editing
   - Enterprise deployment
   - Mission-critical workflows
```

### **Enterprise-Ready Pattern**

```
Category: PROFESSIONAL SOFTWARE
Maturity: Production Release
Standards: Similar to Premiere Pro, DaVinci Resolve, Office

Requirements:
   ✅ Automatic backups before migrations
   ✅ Schema version tracking
   ✅ Migration rollback capability
   ✅ User-facing upgrade notifications
   ✅ Detailed migration logs
   ✅ Support for 2-3 recent versions
   ✅ Cloud sync (optional but recommended)
```

---

## 📌 Decision Matrix: What Should PodcastVideoEditor Do?

```
Current Phase (MVP):
├─ ✅ Keep local database (AppData\Roaming)
├─ ✅ Simple auto-migration (current approach)
├─ ✅ Local backup before migrations
└─ Status: ADEQUATE

Before Commercial Release (Professional Edition):
├─ ✅ Add schema version tracking
├─ ✅ Create backup before each migration
├─ ✅ Support rollback on failure
├─ ✅ Show user-friendly upgrade dialogs
├─ ✅ Maintain last 2-3 database versions
└─ Status: REQUIRED

Future Enhancements (Cloud Sync):
├─ ☐ OneDrive / Google Drive backup
├─ ☐ Version history (git-like timeline)
├─ ☐ Team collaboration (Cloud DB)
├─ ☐ Selective project sharing
└─ Status: OPTIONAL / FUTURE
```

---

## 📋 Summary

### **Now (MVP Phase) ✅**
- Database location: `C:\Users\{User}\AppData\Roaming\PodcastVideoEditor\app.db`
- Suitable for: Single user, local development, internal testing
- Risk level: Acceptable for non-critical data

### **Before Commercial Release ⭐**
- Add version tracking
- Create automatic backups
- Implement rollback capability
- Mirror patterns used by Adobe, Microsoft, Autodesk

### **Enterprise (Future) 🚀**
- Cloud sync (OneDrive / Google Drive)
- Team collaboration
- Project version history
- Professional-grade disaster recovery
