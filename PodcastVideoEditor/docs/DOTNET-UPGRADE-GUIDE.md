# .NET 8.0 to .NET 9/10 LTS Upgrade Guide

**Date**: March 31, 2026  
**Current Version**: .NET 8.0  
**Target Version**: .NET 9 (current) or .NET 10 (LTS - recommended)  
**Project Type**: WPF Desktop Application

## Executive Summary

This document provides a comprehensive guide for upgrading the PodcastVideoEditor project from .NET 8.0 to the latest LTS version. The project consists of:
- **PodcastVideoEditor.Core** - Class library with database, models, and core business logic
- **PodcastVideoEditor.Ui** - WPF desktop application using MVVM pattern
- **Test Projects** - xUnit test suites for both Core and Ui projects

### Upgrade Timeline

**Why Upgrade?**
- .NET 9 (Current release) provides performance improvements and new features
- .NET 10 LTS will provide 3 years of support (until November 2028)
- Security patches and performance enhancements
- WPF improvements and better compatibility with modern Windows

---

## Current Project State

### Projects to Upgrade

| Project | Current TFM | Target TFM | Type |
|---------|-------------|------------|------|
| PodcastVideoEditor.Core | net8.0 | net9.0 or net10.0 | Class Library |
| PodcastVideoEditor.Ui | net8.0-windows | net9.0-windows or net10.0-windows | WPF App |
| PodcastVideoEditor.Core.Tests | net8.0 | net9.0 or net10.0 | xUnit Tests |
| PodcastVideoEditor.Ui.Tests | net8.0-windows | net9.0-windows or net10.0-windows | xUnit Tests |

### Current NuGet Dependencies Analysis

#### Core Project Dependencies
```
CommunityToolkit.Mvvm              8.2.2
Microsoft.EntityFrameworkCore.Sqlite   8.0.2
NAudio                             2.2.1
NAudio.Extras                      2.2.1
Serilog                            4.0.0
Serilog.Sinks.Console             6.1.1
Serilog.Sinks.File                6.0.0
SkiaSharp                          2.88.8
Xabe.FFmpeg                        6.0.2
```

#### UI Project Dependencies
```
CommunityToolkit.Mvvm              8.2.2
gong-wpf-dragdrop                  4.0.0
Microsoft.EntityFrameworkCore.Design   8.0.2
Microsoft.Extensions.Configuration.Binder  8.0.0
Microsoft.Extensions.Configuration.Json    8.0.0
Microsoft.Extensions.DependencyInjection   8.0.0
SkiaSharp.Views.WPF               2.88.8
```

#### Test Project Dependencies
```
Microsoft.NET.Test.Sdk            17.11.1
xunit                             2.9.2
xunit.runner.visualstudio         2.8.2
coverlet.collector                6.0.2
```

---

## Upgrade Steps

### Step 1: Prepare for Upgrade

#### 1.1 Environment Setup
```powershell
# Install .NET 9.0 SDK or .NET 10.0 SDK
# Go to https://dotnet.microsoft.com/download
# Download and install the appropriate SDK for your OS

# Verify installation
dotnet --list-sdks
```

#### 1.2 Source Control
Before making changes, create a clean branch:
```bash
git checkout -b feature/dotnet-upgrade
git status  # Ensure working directory is clean
```

#### 1.3 Backup Current State
```bash
git tag pre-dotnet-upgrade
```

---

### Step 2: Update Target Framework Monikers (TFMs)

Update all `.csproj` files:

#### 2.1 PodcastVideoEditor.Core.csproj
```xml
<!-- FROM -->
<TargetFramework>net8.0</TargetFramework>

<!-- TO -->
<TargetFramework>net9.0</TargetFramework>
<!-- OR for LTS -->
<TargetFramework>net10.0</TargetFramework>
```

#### 2.2 PodcastVideoEditor.Ui.csproj
```xml
<!-- FROM -->
<TargetFramework>net8.0-windows</TargetFramework>

<!-- TO -->
<TargetFramework>net9.0-windows</TargetFramework>
<!-- OR for LTS -->
<TargetFramework>net10.0-windows</TargetFramework>
```

#### 2.3 Test Projects
Apply the same changes to:
- PodcastVideoEditor.Core.Tests.csproj
- PodcastVideoEditor.Ui.Tests.csproj

---

### Step 3: NuGet Package Updates

After changing target frameworks, update all NuGet packages for compatibility:

#### 3.1 Check for Updates
```powershell
cd src
dotnet nuget locals all --clear
dotnet restore PodcastVideoEditor.sln
```

#### 3.2 Recommended Package Updates

**For EntityFrameworkCore (Breaking Change Alert!)**
```xml
<!-- Current -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.2" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.2" />

<!-- Update to -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0" />
```

**Microsoft.Extensions Packages**
```xml
<!-- Current -->
<PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />

<!-- Update to -->
<PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
```

**Other Dependencies (Usually Compatible)**
```xml
CommunityToolkit.Mvvm              → 8.2.2+ (compatible with .NET 9/10)
gong-wpf-dragdrop                  → 4.0.0+ (compatible)
Serilog                            → 4.0.0+ (compatible)
NAudio                             → 2.2.1+ (compatible)
SkiaSharp                          → 2.88.8+ (consider updating to 3.x)
Xabe.FFmpeg                        → 6.0.2+ (compatible)
```

#### 3.3 Update Using Package Manager

Option A: Visual Studio Package Manager Console
```powershell
Update-Package -ProjectName PodcastVideoEditor.Core
Update-Package -ProjectName PodcastVideoEditor.Ui
Update-Package -ProjectName PodcastVideoEditor.Core.Tests
Update-Package -ProjectName PodcastVideoEditor.Ui.Tests
```

Option B: dotnet CLI (Manual updates)
```powershell
cd src/PodcastVideoEditor.Core
dotnet package update
cd ../..
```

---

## Breaking Changes & Migration Issues

### EntityFrameworkCore 8.0 → 9.0 Breaking Changes

#### Issue 1: DbContext Lazy Loading Changes
**Symptom**: Null reference exceptions when accessing navigation properties

**Solution**: Ensure lazy loading is explicitly configured if used:
```csharp
// In DbContext configuration
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    optionsBuilder
        .UseSqlite("Data Source=podcast.db")
        .UseLazyLoadingProxies();  // Enable if needed
}
```

#### Issue 2: Query Compilation Performance
**Symptom**: Slower compilation on first run

**Solution**: Pre-compile queries using EF Core 9 features for better performance
```csharp
// Use compiled queries for frequently executed queries
private static readonly Func<DbContext, int, Task<Podcast>> GetPodcastById =
    EF.CompileAsyncQuery((DbContext db, int id) => db.Podcasts.First(p => p.Id == id));
```

### WPF Improvements in .NET 9/10

**Breaking Changes**: Minimal - WPF is generally backward compatible
**New Features in .NET 10**:
- Better high-DPI support
- Improved performance with modern hardware
- Enhanced XAML compilation

**Recommended Changes**:
```xml
<!-- In PodcastVideoEditor.Ui.csproj, consider adding -->
<PropertyGroup>
    <UseWinRTProjections>true</UseWinRTProjections>
</PropertyGroup>
```

---

## Build & Test Strategy

### Step 4: Verify Build

```powershell
cd src
dotnet clean
dotnet restore PodcastVideoEditor.sln
dotnet build PodcastVideoEditor.sln --configuration Release
```

**Expected Warnings/Errors to Watch For**:
- Obsolete API warnings (update code to use new APIs)
- Package compatibility warnings
- WPF asset compilation warnings (usually safe to ignore)

### Step 5: Run Tests

```powershell
# Run all tests
dotnet test PodcastVideoEditor.sln

# Run specific project tests
dotnet test src/PodcastVideoEditor.Core.Tests
dotnet test src/PodcastVideoEditor.Ui.Tests

# Run with code coverage
dotnet test PodcastVideoEditor.sln /p:CollectCoverage=true
```

**Expected Test Behavior**:
- All existing tests should pass without modification
- Performance should be similar or better
- No functional breaking changes for this project

### Step 6: Application Testing

```powershell
# Publish for testing
dotnet publish src/PodcastVideoEditor.Ui -c Release -f net9.0-windows

# Manual testing checklist:
# ✓ Application starts without errors
# ✓ Audio playback works
# ✓ Video timeline renders correctly
# ✓ Database read/write operations function
# ✓ File import/export operations work
# ✓ No memory leaks during extended use
# ✓ Performance is acceptable (no heavy slowdowns)
```

---

## Known Issues & Workarounds

### Issue 1: SkiaSharp Version Compatibility
**Status**: Monitor for updates
```xml
<!-- Current version is compatible, but consider upgrading -->
<PackageReference Include="SkiaSharp" Version="2.88.8" />
<!-- Newer: -->
<PackageReference Include="SkiaSharp" Version="3.x.x" />
```

### Issue 2: NAudio Compatibility
**Status**: Compatible with .NET 8/9/10
**Workaround**: No action needed

### Issue 3: Xabe.FFmpeg with Media Files
**Status**: Compatible
**Check**: Ensure ffmpeg binaries are still compatible (third_party/ffmpeg folder)

---

## Rollback Plan

If issues occur after upgrade:

```bash
# Revert to pre-upgrade state
git checkout pre-dotnet-upgrade

# Or manually revert TFMs:
# - Change net9.0 back to net8.0
# - Change net9.0-windows back to net8.0-windows
# - Revert NuGet packages to previous versions

# Clear build artifacts
dotnet clean
```

---

## Performance Testing Recommendations

After upgrade, validate performance:

```csharp
// Add performance diagnostics to measure
var sw = System.Diagnostics.Stopwatch.StartNew();

// ... your code ...

sw.Stop();
Debug.WriteLine($"Operation took {sw.ElapsedMilliseconds}ms");
```

**Expected Performance**:
- Startup time: Similar or slightly faster
- Video rendering: Similar or faster (improved compiler)
- Database queries: Similar or faster (potential EF Core 9 improvements)
- Memory usage: Similar or slightly lower

---

## Deployment Checklist

- [ ] All projects build successfully
- [ ] All tests pass
- [ ] Manual testing completed
- [ ] No console errors during execution
- [ ] NuGet packages resolve without conflict warnings
- [ ] Application publishes successfully
- [ ] Installer updated (if applicable)
- [ ] Release notes prepared
- [ ] Git repository tagged with final version
- [ ] Backup of .NET 8 version created

---

## Version Support Timeline

| Version | Release Date | End of Support |
|---------|--------------|---|
| .NET 8 (LTS) | Nov 2023 | Nov 2025 |
| .NET 9 (Current) | Nov 2024 | May 2025 |
| .NET 10 (LTS) | Nov 2025 | Nov 2028 |

**Recommendation**: Upgrade to .NET 10 LTS for long-term support and stability.

---

## Additional Resources

- [.NET 9 Migration Guide](https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0)
- [.NET 10 Migration Guide](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0)
- [Entity Framework Core 9.0 Changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/breaking-changes)
- [WPF in .NET](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
- [NuGet Package Search](https://www.nuget.org/)

---

## Questions & Troubleshooting

### Q: Will my database still work?
**A**: Yes. The upgrade is internal to the application. Your SQLite database files will work unchanged.

### Q: Do I need to recompile plugins or extensions?
**A**: Yes, if you have any native interop or external assemblies, they must target compatible or be recompiled for the new .NET version.

### Q: Will user settings be preserved?
**A**: Yes. Application settings and configuration files are preserved across .NET versions.

### Q: Is this upgrade mandatory?
**A**: No, but recommended for security updates and performance improvements. .NET 8 will be supported until November 2025.

---

## Contact & Support

For specific issues during upgrade:
1. Check the [Known Issues](#known-issues--workarounds) section
2. Review build and test output for specific error messages
3. Consult package documentation at nuget.org
4. Check Microsoft's .NET documentation

---

**Last Updated**: March 31, 2026  
**Status**: Ready for Upgrade Execution
