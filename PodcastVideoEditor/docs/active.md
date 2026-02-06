# Active Task Pack - Phase 1

## Current Phase: Phase 1 - Core Engine & Audio Management

**Duration Target:** 2-3 weeks (Feb 6 - Feb 27, 2026)

---

## Task Pack: TP-001-CORE

### Overview
Xây dựng nền tảng: Project structure, Database, Audio management, basic render pipeline.

### Subtasks (ST)

#### ST-1: Project Setup & Dependencies
**Objective:** Tạo solution WPF .NET 8, cấu trúc thư mục MVVM, cài NuGet packages.

- [ ] Create WPF .NET 8 solution (PodcastVideoEditor.sln)
  - [ ] PodcastVideoEditor.Core (Class Library)
  - [ ] PodcastVideoEditor.Ui (WPF App)
  - [ ] PodcastVideoEditor.Tests (xUnit) - optional for phase 1

- [ ] Install NuGet packages:
  - [ ] CommunityToolkit.Mvvm
  - [ ] EntityFrameworkCore, SQLite (Microsoft.EntityFrameworkCore.Sqlite)
  - [ ] NAudio, NAudio.Extras
  - [ ] SkiaSharp, SkiaSharp.Views.WPF
  - [ ] Xabe.FFmpeg
  - [ ] Serilog, Serilog.Sinks.File
  - [ ] Refit (HTTP client for future API calls)

- [ ] Create folder structure:
  ```
  Core/
    ├── Models/
    ├── Services/
    ├── Database/
    ├── Utilities/
    └── ...
  Ui/
    ├── Views/
    ├── ViewModels/
    ├── Controls/
    ├── Resources/
    └── ...
  ```

- [ ] Setup Serilog logging to file (AppData/Logs/app.log)

**Acceptance Criteria:**
- Solution compiles without errors
- All NuGet packages resolved
- Logger writes to file
- Folder structure created

---

#### ST-2: Database Schema & Models
**Objective:** Design SQLite schema, create EF Core DbContext, define C# models.
**Status:** ✅ **COMPLETED**

**Models Created:**
```csharp
✅ Project { Id, Name, CreatedAt, AudioPath, RenderSettings }
✅ Segment { Id, ProjectId, StartTime, EndTime, Text, Order, TransitionType, TransitionDuration }
✅ Element { Id, ProjectId, Type, X, Y, Width, Height, Properties }
✅ Asset { Id, ProjectId, FilePath, FileName, Extension, FileSize, Type }
✅ BgmTrack { Id, ProjectId, AudioPath, Volume, FadeInSeconds, FadeOutSeconds }
✅ Template { Id, Name, LayoutJson, CreatedAt }
```

**Completed Tasks:**
- [x] Created DbContext (AppDbContext) with DbSets
- [x] Configured all relationships (FK with CASCADE delete)
- [x] Implemented IDesignTimeDbContextFactory for migrations
- [x] Created EF Core Migrations (InitialCreate)
- [x] Applied migration → SQLite database created
- [x] Verified schema with sqlite3 CLI
- [x] Tested CRUD operations (Create, Read, Update, Delete)
- [x] Repository pattern - deferred to phase 2 (kept simple for MVP)

**Acceptance Criteria:** ✅ ALL MET
- [x] Database can be created via EF migrations
- [x] All models map correctly to tables
- [x] FK relationships defined with CASCADE delete
- [x] Sample insert/query works perfectly
- [x] CRUD test suite passes (9/9 operations)
- [x] Schema verified with indexes for performance

**Database Location:** `C:\Users\DUC CANH PC\AppData\Roaming\PodcastVideoEditor\app.db` (86 KB)

---

#### ST-3: Audio Service & NAudio Integration
**Objective:** Audio upload, play, pause, seek, get duration + FFT data.
**📖 REFERENCE:** See `docs/reference-sources.md` → NAudio WPF Samples section
- Study waveform rendering patterns
- Study audio position sync logic
- [ ] Create AudioService class:
  - [ ] LoadAudio(filePath) -> returns AudioMetadata { Duration, Samplerate, Channels }
  - [ ] Play() / Pause() / Stop()
  - [ ] Seek(position)
  - [ ] GetCurrentPosition() -> TimeSpan
  - [ ] GetFFTData() -> float[] (for visualizer)

- [ ] Create UI for audio upload:
  - [ ] Button "Select Audio File"
  - [ ] File Dialog (*.mp3, *.wav, *.m4a)
  - [ ] Copy file to AppData (local cache)
  - [ ] Display audio duration, name

- [ ] Create AudioPlayer ViewModel (Mvvm):
  - [ ] CurrentAudio (INotifyPropertyChanged)
  - [ ] CurrentPosition (TimeSpan)
  - [ ] IsPlaying (bool)
  - [ ] Commands: PlayCommand, PauseCommand, etc.

- [ ] Test with sample audio files (10s, 60s, 5min)

**Acceptance Criteria:**
- Audio file can be loaded and played
- Duration and position display correctly
- FFT data can be extracted
- No memory leaks when loading multiple files

---

#### ST-4: Database Setup & Project CRUD
**Objective:** Create, read, update, delete projects in SQLite.

- [ ] Implement ProjectService:
  - [ ] CreateProject(name, audioPath) -> Project
  - [ ] LoadProject(projectId) -> Project with all relations
  - [ ] SaveProject(project)
  - [ ] DeleteProject(projectId)
  - [ ] ListProjects() -> IEnumerable<Project>

- [ ] Create ProjectViewModel (MVVM):
  - [ ] CurrentProject (Project)
  - [ ] Projects (ObservableCollection)
  - [ ] Commands: NewProject, OpenProject, SaveProject

- [ ] Create "New Project" dialog:
  - [ ] Input project name
  - [ ] Select audio file
  - [ ] Create project in DB

**Acceptance Criteria:**
- Project can be created and saved to DB
- Project can be loaded from DB
- All relations (segments, elements, assets) load correctly
- Delete project removes from DB

---

#### ST-5: Basic Render Pipeline (Audio Only)
**Objective:** FFmpeg wrapper untuk output MP4 dari 1 audio + 1 static image.

- [ ] Create FFmpegService class:
  - [ ] DetectFFmpeg() -> Validate installed, get path from config
  - [ ] RenderVideo(config: RenderConfig) -> Task<string> (output path)
    - Input: audio, image, resolution, quality
    - Output: MP4 file
  - [ ] ReportProgress via IProgress<RenderProgress>

- [ ] Create RenderConfig model:
  ```csharp
  class RenderConfig {
    string AudioPath;
    string ImagePath;
    string OutputPath;
    int ResolutionWidth; // 1080
    int ResolutionHeight; // 1920
    string Quality; // Low/Medium/High -> CRF value
  }
  ```

- [ ] Create Render UI (Render View):
  - [ ] Dropdown: Resolution (1080p, 720p, 480p), Aspect (9:16, 16:9, 1:1)
  - [ ] Dropdown: Quality (Low/Medium/High)
  - [ ] Button "Start Render"
  - [ ] ProgressBar + ETA (optional for phase 1)
  - [ ] Cancel button

- [ ] Test render with sample files
  - [ ] Verify output MP4 plays correctly
  - [ ] Verify resolution/aspect ratio

**Acceptance Criteria:**
- FFmpeg detected and validated on machine
- Render produces valid MP4
- Resolution and quality settings applied
- Progress reported correctly
- Can cancel render

---

#### ST-6: MVP UI Layout
**Objective:** Create basic MainWindow layout: Home, Editor placeholder, Settings.

- [ ] Create MainWindow.xaml:
  - [ ] Menu bar: File, Edit, Help
  - [ ] Tab control: Home, Editor, Settings

- [ ] Home Tab:
  - [ ] Button "New Project" (opens dialog from ST-4)
  - [ ] Button "Open Project" (loads from dialog)
  - [ ] ListBox "Recent Projects"

- [ ] Editor Tab (placeholder for now):
  - [ ] AudioPlayer control (from ST-3)
  - [ ] Button "Render" (from ST-5)
  - [ ] Status text

- [ ] Settings Tab:
  - [ ] TextBox "FFmpeg Path" (validate)
  - [ ] TextBox "App Data Path"
  - [ ] Button "Save Settings"

- [ ] Theme: Light/Dark (optional)

**Acceptance Criteria:**
- App launches without errors
- Navigation between tabs works
- Settings can be saved/loaded

---

### Dependencies Between Subtasks

```
ST-1 (Setup) 
  ↓
ST-2 (Database) → ST-4 (CRUD)
  ↓
ST-3 (Audio) → ST-5 (Render)
  ↓
ST-6 (UI) - integrates all above
```

---

### Test Plan (Phase 1)

**Manual Tests:**
- [ ] Launch app, create new project with audio file
- [ ] Play audio, seek to different positions
- [ ] Render with 1080p + static image
- [ ] Verify output video duration matches audio
- [ ] Check audio sync (play 60s audio, render, verify playback)

**Edge Cases:**
- [ ] Audio file with non-standard format (96kHz, mono)
- [ ] Very long audio (1+ hour)
- [ ] FFmpeg not installed → graceful error
- [ ] Cancel render mid-process → cleanup

---

### Deliverables (Phase 1 End)

1. **Source Code:**
   - WPF app that can load audio, render MP4
   - All models, services, ViewModels

2. **Database:**
   - SQLite file with schema
   - EF Core migrations

3. **Configuration:**
   - appsettings.json template
   - Logging configured

4. **Documentation:**
   - code_rules.md updated
   - API contracts documented (FFmpegService, AudioService)
   - Known issues listed in issues.md

---

### Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| FFmpeg not installed | 🔴 Blocker | Validate on startup, suggest download link |
| Audio file format unsupported | 🟡 Medium | Use NAudio.Extras, test common formats |
| Async deadlock in audio callback | 🔴 Blocker | Use TaskScheduler.FromCurrentSynchronizationContext() |
| Database file corruption | 🟡 Medium | Backup on startup, use transactions |

---

## Current Work Status

### Phase 1 Progress
- [x] ST-1: 100% (✅ DONE - 2026-02-06)
- [ ] ST-2: 0% (TODO)
- [ ] ST-3: 0% (TODO)
- [ ] ST-4: 0% (TODO)
- [ ] ST-5: 0% (TODO)
- [ ] ST-6: 0% (TODO)

**Phase 1 Overall: 17% (1/6 tasks complete)**

---

## Resume Instructions (If PAUSE)

If work pauses at any point during Phase 1:
1. Update this file with completed subtask % and blockers
2. Note any code branch/commit hash
3. List any open questions or decisions needed
4. Update docs/issues.md with blockers

Resume by re-reading this file, then jumping to next uncompleted ST.

---

Last updated: 2026-02-06
Next review: Daily during active work
