# .NET 8.0 → .NET 9.0/10.0 Upgrade - Action Items Checklist

**Project**: PodcastVideoEditor  
**Current Version**: .NET 8.0  
**Target Version**: .NET 9.0 (Current) or .NET 10.0 LTS (Recommended)  
**Prepared**: March 31, 2026  
**Status**: Ready for Implementation

---

## Pre-Upgrade Preparation

### Environment & Setup
- [ ] **Verify Current .NET Installation**
  - Command: `dotnet --list-sdks`
  - Expected: Shows 8.0.x SDK installed
  - Document: Current SDK version: ___________________

- [ ] **Download Required .NET SDK**
  - Go to: https://dotnet.microsoft.com/download
  - Download: .NET 9.0 SDK or .NET 10.0 LTS SDK
  - Platform: Windows x64
  - Document: Target version: ___________________

- [ ] **Install New .NET SDK**
  - Run installer
  - Accept default installation path or specify alternate
  - Complete installation
  - Verify: `dotnet --list-sdks` should show new version

- [ ] **Update Visual Studio (if using)**
  - Visual Studio Code: Extensions → C# DevKit update
  - Visual Studio 2022: Check for updates
  - Document: IDE version: ___________________

---

## Source Control & Backup

- [ ] **Create Feature Branch**
  ```bash
  git checkout -b feature/dotnet-9-upgrade
  ```
  - Confirm branch created: `git branch --show-current`
  - Document: Branch name: `feature/dotnet-9-upgrade`

- [ ] **Commit Current State**
  ```bash
  git add -A
  git commit -m "WIP: Pre-upgrade baseline for .NET upgrade"
  ```
  - Confirm: `git log -1 --oneline`

- [ ] **Create Backup Tag**
  ```bash
  git tag pre-dotnet-8-upgrade
  ```
  - Confirm: `git tag -l | grep pre-dotnet`

- [ ] **Verify Clean Working Directory**
  ```bash
  git status
  ```
  - Expected: "nothing to commit, working tree clean"

---

## Update Project Files

### Update Target Framework Monikers

- [ ] **PodcastVideoEditor.Core.csproj**
  - File Path: `src/PodcastVideoEditor.Core/PodcastVideoEditor.Core.csproj`
  - Line: `<TargetFramework>net8.0</TargetFramework>`
  - Change to: `<TargetFramework>net9.0</TargetFramework>` (or `net10.0` for LTS)
  - Verification: Open file and confirm change

- [ ] **PodcastVideoEditor.Ui.csproj**
  - File Path: `src/PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj`
  - Line: `<TargetFramework>net8.0-windows</TargetFramework>`
  - Change to: `<TargetFramework>net9.0-windows</TargetFramework>` (or `net10.0-windows`)
  - Verification: Open file and confirm change

- [ ] **PodcastVideoEditor.Core.Tests.csproj**
  - File Path: `src/PodcastVideoEditor.Core.Tests/PodcastVideoEditor.Core.Tests.csproj`
  - Line: `<TargetFramework>net8.0</TargetFramework>`
  - Change to: `<TargetFramework>net9.0</TargetFramework>` (or `net10.0`)
  - Verification: Open file and confirm change

- [ ] **PodcastVideoEditor.Ui.Tests.csproj**
  - File Path: `src/PodcastVideoEditor.Ui.Tests/PodcastVideoEditor.Ui.Tests.csproj`
  - Line: `<TargetFramework>net8.0-windows</TargetFramework>`
  - Change to: `<TargetFramework>net9.0-windows</TargetFramework>` (or `net10.0-windows`)
  - Verification: Open file and confirm change

- [ ] **Commit Changes**
  ```bash
  git add src/*.csproj src/*/*.csproj
  git commit -m "chore: update target framework to .NET 9.0"
  ```

---

## NuGet Package Updates

### Clear Cache

- [ ] **Clear NuGet Cache**
  ```bash
  dotnet nuget locals all --clear
  ```
  - Confirm: Cache cleared message appears

### Restore Packages

- [ ] **Restore Solution**
  ```bash
  cd src
  dotnet restore PodcastVideoEditor.sln
  ```
  - Expected: No errors (warnings are OK)
  - Document any warnings: ___________________

### Update EntityFrameworkCore (Priority 1)

- [ ] **Update EF Core Sqlite**
  - File: `src/PodcastVideoEditor.Core/PodcastVideoEditor.Core.csproj`
  - Current: `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.2" />`
  - Update to: `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />`
  - Verification: Confirmed in file

- [ ] **Update EF Core Design**
  - File: `src/PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj`
  - Current: `<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.2" />`
  - Update to: `<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0" />`
  - Verification: Confirmed in file

### Update Microsoft.Extensions (Priority 2)

- [ ] **Update Configuration.Binder**
  - File: `src/PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj`
  - Current: `<PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.0" />`
  - Update to: `<PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="9.0.0" />`

- [ ] **Update Configuration.Json**
  - File: `src/PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj`
  - Current: `<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />`
  - Update to: `<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.0" />`

- [ ] **Update DependencyInjection**
  - File: `src/PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj`
  - Current: `<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />`
  - Update to: `<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />`

### Optional Package Updates (Priority 3)

- [ ] **Consider NAudio Updates** (Optional)
  - Current: 2.2.1
  - Can update to: 2.2.2 or latest 2.2.x
  - Decision: Keep at 2.2.1 / Update to ____________

- [ ] **Consider SkiaSharp Updates** (Optional)
  - Current: 2.88.8
  - Can update to: 3.x (requires more testing)
  - Decision: Keep at 2.88.8 / Update to ____________

- [ ] **Commit Package Changes**
  ```bash
  git add src/*.csproj src/*/*.csproj
  git commit -m "chore: update NuGet packages for .NET 9.0 compatibility"
  ```

---

## Build & Verification

### Clean Build

- [ ] **Clean All Artifacts**
  ```bash
  dotnet clean
  ```
  - Confirm: "completely clean" message or directory cleared

- [ ] **Restore Solution**
  ```bash
  dotnet restore PodcastVideoEditor.sln
  ```
  - Expected: All packages restored
  - Any errors? Document: ___________________

### Build Solution

- [ ] **Build Debug Configuration**
  ```bash
  dotnet build PodcastVideoEditor.sln --configuration Debug
  ```
  - Result: ✅ Success / ❌ Errors
  - Error count: ___________________
  - Document any breaking change errors: ___________________

- [ ] **Build Release Configuration**
  ```bash
  dotnet build PodcastVideoEditor.sln --configuration Release
  ```
  - Result: ✅ Success / ❌ Errors
  - Error count: ___________________

### Address Breaking Changes

- [ ] **Review Any EF Core 9.0 Breaking Changes**
  - Check: Are there any lazy-loading issues?
  - Check: Are there query compilation warnings?
  - Fix any issues found: ___________________

- [ ] **Run Code Analysis**
  ```bash
  dotnet build PodcastVideoEditor.sln --parameters /p:TreatWarningsAsErrors=true
  ```
  - Result: ✅ Success / ❌ Errors
  - Fix any warnings elevated to errors

---

## Testing

### Unit Tests - Core

- [ ] **Run Core Tests**
  ```bash
  dotnet test src/PodcastVideoEditor.Core.Tests --verbosity normal
  ```
  - Tests run: _____ / _____ passed
  - Failures: ______________________
  - Skipped: ______________________

- [ ] **Verify Coverage**
  ```bash
  dotnet test src/PodcastVideoEditor.Core.Tests /p:CollectCoverage=true
  ```
  - Coverage %: ______
  - New coverage gaps? ___________________

### Unit Tests - UI

- [ ] **Run UI Tests**
  ```bash
  dotnet test src/PodcastVideoEditor.Ui.Tests --verbosity normal
  ```
  - Tests run: _____ / _____ passed
  - Failures: ______________________
  - WPF-specific issues: ___________________

### Integration Tests

- [ ] **Run All Tests**
  ```bash
  dotnet test PodcastVideoEditor.sln --verbosity normal
  ```
  - Total tests: _____
  - Passed: _____
  - Failed: _____
  - Skipped: _____

- [ ] **Test Results Review**
  - All tests passing? ✅ YES / ❌ NO
  - Performance regression? ✅ NO / ❌ YES (describe: _______)

---

## Manual Application Testing

### Publish Application

- [ ] **Publish for Testing**
  ```bash
  dotnet publish src/PodcastVideoEditor.Ui -c Release
  ```
  - Publish location: `src/PodcastVideoEditor.Ui/bin/Release/net9.0-windows/publish/`
  - Expected: No errors

### Runtime Testing Checklist

- [ ] **Application Launch**
  - Run: `PodcastVideoEditor.Ui.exe`
  - Starts without errors? ✅ YES / ❌ NO
  - Issues: ___________________

- [ ] **Main UI Elements**
  - Timeline loads? ✅ YES / ❌ NO
  - Controls responsive? ✅ YES / ❌ NO
  - Menus accessible? ✅ YES / ❌ NO

- [ ] **Audio Playback**
  - Load a podcast file: ✅ SUCCESS / ❌ FAILED
  - Play button works? ✅ YES / ❌ NO
  - Sound output? ✅ YES / ❌ NO
  - Pause/Resume? ✅ YES / ❌ NO
  - Seek functionality? ✅ YES / ❌ NO

- [ ] **Video Timeline**
  - Video renders? ✅ YES / ❌ NO
  - Waveform displays? ✅ YES / ❌ NO
  - Segments show correctly? ✅ YES / ❌ NO
  - Drag-drop works? ✅ YES / ❌ NO
  - Track editing works? ✅ YES / ❌ NO

- [ ] **File Operations**
  - Import file: ✅ SUCCESS / ❌ FAILED
  - Export video: ✅ SUCCESS / ❌ FAILED
  - Save project: ✅ SUCCESS / ❌ FAILED
  - Load project: ✅ SUCCESS / ❌ FAILED

- [ ] **Database Operations**
  - Create new podcast: ✅ SUCCESS / ❌ FAILED
  - Save podcast: ✅ SUCCESS / ❌ FAILED
  - Load podcast: ✅ SUCCESS / ❌ FAILED
  - Delete podcast: ✅ SUCCESS / ❌ FAILED
  - User data preserved: ✅ YES / ❌ NO

- [ ] **Performance Baseline**
  - Startup time: _____ seconds (was: _____ seconds)
  - Auto-save lag: ✅ ACCEPTABLE / ❌ NOTICEABLE
  - Rendering FPS: _____ (was: _____)
  - Memory usage: _____ MB (was: _____ MB)

- [ ] **Extended Run Test**
  - Run for 30 minutes: ✅ PASSED / ❌ FAILED
  - Memory leak detected? ✅ NO / ❌ YES
  - Crashes occurred? ✅ NO / ❌ YES (describe: _______)
  - Console errors? ✅ NONE / ❌ YES (describe: _______)

- [ ] **Edge Cases**
  - Large file (>1GB): ✅ HANDLED / ❌ ISSUES
  - Many segments (100+): ✅ HANDLED / ❌ ISSUES
  - Rapid UI interactions: ✅ STABLE / ❌ CRASHES
  - Network latency simulation: N/A

---

## Documentation & Deployment

### Update Documentation

- [ ] **Review Generated Docs**
  - ✅ DOTNET-UPGRADE-GUIDE.md created
  - ✅ DOTNET-UPGRADE-COMPATIBILITY-REPORT.md created
  - ✅ ACTION-ITEMS-CHECKLIST.md created

- [ ] **Update README.md**
  - Add note: Upgrade completed to .NET 9.0 / 10.0
  - Update build instructions if needed
  - Update system requirements section

- [ ] **Update INSTALL-UPDATE-GUIDE.md**
  - Note minimum .NET version required
  - Update installation prerequisites

- [ ] **Create Release Notes**
  - Summary: Updated to .NET 9.0 / .NET 10.0 LTS
  - Benefits: Performance improvements, security updates
  - Known issues: (none expected)
  - Breaking changes: (none expected for users)

### Git Commits & Tagging

- [ ] **Create Migration Commit**
  ```bash
  git add -A
  git commit -m "feat: upgrade to .NET 9.0 LTS"
  ```
  Note the commit hash: _______________

- [ ] **Create Version Tag**
  ```bash
  git tag v1.0.3-dotnet9
  ```
  Or for LTS:
  ```bash
  git tag v1.0.3-dotnet10-lts
  ```

- [ ] **Verify Git History**
  ```bash
  git log --oneline -5
  git tag -l | grep dotnet
  ```

### Build Artifacts

- [ ] **Create Release Build**
  ```bash
  dotnet publish src/PodcastVideoEditor.Ui -c Release -f net9.0-windows --self-contained false
  ```
  - Publish successful? ✅ YES / ❌ NO
  - Location: `src/PodcastVideoEditor.Ui/bin/Release/net9.0-windows/publish/`

- [ ] **Create Installer (if applicable)**
  - Run: `build.bat` or `Build-Release.ps1`
  - Installer created? ✅ YES / ❌ NO
  - Location: ___________________

- [ ] **Archive Artifacts**
  - Create backup: `artifacts/`
  - Backup timestamp: ___________________

---

## Final Validation

### Pre-Release Checklist

- [ ] **All Tests Passing**: _____ / _____ tests passing
- [ ] **Build Successful**: Debug ✅ Release ✅
- [ ] **Application Launches**: ✅ YES / ❌ NO
- [ ] **Core Features Working**: ✅ 100% / ❌ Issues: _______
- [ ] **No Regressions**: ✅ CONFIRMED / ❌ ISSUES: _______
- [ ] **Documentation Updated**: ✅ YES / ❌ NO
- [ ] **Git History Clean**: ✅ YES / ❌ NO
- [ ] **Backup Tag Created**: ✅ YES / ❌ NO

### Sign-Off

- [ ] **All Checks Passed**: ✅ YES / ❌ NO IF NO, DON'T PROCEED

**Upgraded By**: ___________________________  
**Date**: ___________________________  
**Time**: __________________________  

---

## Rollback Procedure (If Needed)

- [ ] **Identify Issue**
  - Description: ___________________________

- [ ] **Stop Application**
  - Shutdown running instances

- [ ] **Revert Changes**
  ```bash
  git checkout pre-dotnet-8-upgrade
  ```
  - Verify: Same version as before? ✅ YES / ❌ NO

- [ ] **Clean Build**
  ```bash
  dotnet clean && dotnet restore && dotnet build
  ```

- [ ] **Verify Rollback**
  - Tests passing? ✅ YES / ❌ NO
  - Application works? ✅ YES / ❌ NO

- [ ] **Analyze Failure**
  - Document what went wrong: ___________________________
  - Plan fix: ___________________________

---

## Post-Upgrade Maintenance

### Scheduled Tasks

- [ ] **Week 1**: Monitor for any runtime issues
- [ ] **Week 2**: User feedback period
- [ ] **Week 4**: Consider release to production
- [ ] **Monthly**: Monitor NuGet package updates

### Future Upgrades

- [ ] **Plan .NET 10.0 LTS Migration** (if currently on 9.0)
  - Target date: ___________________________
  - Timeline: Before .NET 9.0 end-of-support (May 2025)

- [ ] **Monitor for .NET 11.0 Preview** (Q4 2025)
  - Evaluation planned: ___________________________

---

## Notes & Additional Items

```
Use this section to document any additional concerns, 
custom modifications, or project-specific migration tasks:

_________________________________________________________________

_________________________________________________________________

_________________________________________________________________

_________________________________________________________________

_________________________________________________________________
```

---

## Sign-Off & Review

**Upgrade Initiated**: ___________________________  
**Upgrade Completed**: ___________________________  
**Tested By**: ___________________________  
**Approved For Release**: ___________________________  

---

**Total Estimated Time**: ~2-4 hours  
**Difficulty Level**: LOW-MEDIUM  
**Risk Level**: LOW  
**Status**: READY FOR UPGRADE ✅

---

**Last Updated**: March 31, 2026  
**Version**: 1.0  
**Next Review**: Upon completion of upgrade
