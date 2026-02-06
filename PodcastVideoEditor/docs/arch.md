# Architecture Overview

**Project:** Podcast Video Editor  
**Version:** 1.0 (MVP)  
**Date:** 2026-02-06

---

## System Design (High-Level)

```
┌─────────────────────────────────────────────────────────────────┐
│                       WPF UI Layer (Presentation)                │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ MainWindow   │ │ EditorView   │ │ SettingsView │ ...        │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────┬───────────────────────────────────────────────┘
                  │ MVVM Binding (Data Context)
┌─────────────────┴───────────────────────────────────────────────┐
│                    ViewModel Layer (Logic)                       │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │MainViewModel │ │EditorViewModel│ │ProjectViewModel│...      │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────┬───────────────────────────────────────────────┘
                  │ Dependency Injection
┌─────────────────┴───────────────────────────────────────────────┐
│                    Service Layer (Business Logic)                │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │AudioService  │ │VisualizerServ.│ │FFmpegService│ ...       │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────┬───────────────────────────────────────────────┘
                  │
┌─────────────────┴───────────────────────────────────────────────┐
│              Repository/Database Layer (Data Persistence)        │
│  ┌──────────────┐ ┌──────────────┐                              │
│  │ProjectReposi.│ │AppDbContext  │ ────→ SQLite (app.db)       │
│  └──────────────┘ └──────────────┘                              │
└─────────────────────────────────────────────────────────────────┘

External Services (Async HTTP):
  ├─ FFmpeg (Process.Start, Pipe)
  ├─ Yescale API (AI Segmentation) - Phase 4
  ├─ Unsplash/Pexels/Pixabay API (Image Search) - Phase 4
  └─ File System (Audio, Images, Videos)
```

---

## Layered Architecture Details

### 1. Presentation Layer (WPF)

**Responsibility:** UI rendering, user input, display state

**Components:**
- `Views/` - XAML UI definitions (MainWindow, EditorView, SettingsView, etc.)
- `Controls/` - Custom WPF/Skia controls (VisualizerControl, AudioPlayerControl, CanvasControl)
- `Resources/` - Styles, themes, converters, templates

**Key Files:**
```
Ui/
  ├─ Views/
  │   ├─ MainWindow.xaml (.xaml.cs)
  │   ├─ EditorView.xaml (.xaml.cs)
  │   ├─ TimelineView.xaml
  │   ├─ SettingsView.xaml
  │   └─ ...
  ├─ Controls/
  │   ├─ CanvasControl.xaml (Drag&drop editor surface)
  │   ├─ VisualizerControl.xaml (SkiaSharp spectrum)
  │   ├─ TimelineControl.xaml (Segments + BGM)
  │   ├─ AudioPlayerControl.xaml (Play/Pause/Seek)
  │   └─ ...
  └─ Resources/
      ├─ Styles.xaml
      └─ Themes/
```

**Principles:**
- **No business logic** in code-behind (except event forwarding)
- **MVVM binding** for all state
- **Data converters** for UI-specific formatting

---

### 2. ViewModel Layer (MVVM)

**Responsibility:** Manage UI state, command handling, coordinate services

**Components:**
- `ViewModels/` - One ViewModel per major View
- `Mvvm/` - Custom MVVM helpers (if needed)

**Key Files:**
```
Core/ViewModels/
  ├─ MainViewModel.cs
  ├─ EditorViewModel.cs
  ├─ TimelineViewModel.cs
  ├─ AudioPlayerViewModel.cs
  ├─ CanvasViewModel.cs
  ├─ RenderViewModel.cs
  └─ SettingsViewModel.cs
```

**Pattern (using MVVM Toolkit):**
```csharp
public partial class EditorViewModel : ObservableObject
{
    private readonly IProjectService _projectService;
    private readonly IAudioService _audioService;

    [ObservableProperty]
    private Project currentProject;

    [ObservableProperty]
    private bool isPlaying;

    public EditorViewModel(IProjectService projectService, IAudioService audioService)
    {
        _projectService = projectService;
        _audioService = audioService;
    }

    [RelayCommand]
    public async Task LoadProjectAsync(string projectId)
    {
        CurrentProject = await _projectService.LoadProjectAsync(projectId);
    }
}
```

---

### 3. Service Layer (Business Logic)

**Responsibility:** Core functionality, external integrations, data processing

**Key Services:**

#### IAudioService
```csharp
public interface IAudioService
{
    Task<AudioMetadata> LoadAudioAsync(string filePath);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    TimeSpan CurrentPosition { get; }
    float[] GetFFTData(int length = 256); // For visualizer
}
```

#### IVisualizerService
```csharp
public interface IVisualizerService
{
    void RenderSpectrum(SKCanvas canvas, float[] fftData, VisualizerSettings settings);
    void RenderWaveform(SKCanvas canvas, byte[] waveData, VisualizerSettings settings);
}
```

#### IFFmpegService
```csharp
public interface IFFmpegService
{
    Task<string> RenderVideoAsync(RenderConfig config, IProgress<RenderProgress> progress, CancellationToken ct);
    bool ValidateFFmpegInstallation();
    string GetFFmpegVersion();
}
```

#### IProjectService
```csharp
public interface IProjectService
{
    Task<Project> CreateProjectAsync(string name, string audioPath);
    Task<Project> LoadProjectAsync(string projectId);
    Task SaveProjectAsync(Project project);
    Task DeleteProjectAsync(string projectId);
    Task<IEnumerable<Project>> ListProjectsAsync();
}
```

#### ISegmentService
```csharp
public interface ISegmentService
{
    IEnumerable<Segment> ParseScript(string scriptText); // Parse "[00:00->00:10] text" format
    void AddSegment(Segment segment);
    void UpdateSegment(Segment segment);
    void DeleteSegment(string segmentId);
}
```

#### IAssetService
```csharp
public interface IAssetService
{
    Task<Asset> ImportAssetAsync(string filePath, AssetType type);
    Task<Asset> GetAssetAsync(string assetId);
    Task DeleteAssetAsync(string assetId);
    IEnumerable<Asset> ListAssets(AssetType? filterType = null);
}
```

**Services Folder:**
```
Core/Services/
  ├─ AudioService.cs (Impl: NAudio, FFT extraction)
  ├─ VisualizerService.cs (Impl: SkiaSharp drawing)
  ├─ FFmpegService.cs (Impl: Process + Pipe streaming)
  ├─ ProjectService.cs (Impl: Repository + DB)
  ├─ SegmentService.cs (Impl: Regex parsing, validation)
  ├─ AssetService.cs (Impl: File copy to AppData)
  ├─ RenderService.cs (Impl: Orchestrate FFmpeg + Visualizer)
  └─ ConfigService.cs (Impl: appsettings + LocalAppData storage)
```

---

### 4. Repository/Database Layer

**Responsibility:** Data persistence, queries, relationships

**Components:**

#### DbContext
```csharp
public class AppDbContext : DbContext
{
    public DbSet<Project> Projects { get; set; }
    public DbSet<Segment> Segments { get; set; }
    public DbSet<Element> Elements { get; set; }
    public DbSet<Asset> Assets { get; set; }
    public DbSet<BgmTrack> BgmTracks { get; set; }
    public DbSet<Template> Templates { get; set; }
    
    // OnModelCreating: fluent API, relationships, constraints
}
```

#### Repositories
```
Core/Database/
  ├─ AppDbContext.cs
  ├─ Repositories/
  │   ├─ IProjectRepository.cs
  │   ├─ ProjectRepository.cs
  │   ├─ ISegmentRepository.cs
  │   ├─ SegmentRepository.cs
  │   └─ ...
  └─ Migrations/
      ├─ 20260206_InitialCreate.cs
      └─ ...
```

**Pattern:**
```csharp
public interface IProjectRepository
{
    Task<Project> GetAsync(string id);
    Task<IEnumerable<Project>> ListAsync();
    Task CreateAsync(Project project);
    Task UpdateAsync(Project project);
    Task DeleteAsync(string id);
}

public class ProjectRepository : IProjectRepository
{
    private readonly AppDbContext _context;
    
    public async Task<Project> GetAsync(string id)
    {
        return await _context.Projects
            .Include(p => p.Segments)
            .Include(p => p.Elements)
            .FirstOrDefaultAsync(p => p.Id == id);
    }
}
```

---

## Data Flow (Example: Audio + Visualizer)

```
User selects audio file
  ↓
[EditorView] Button Click
  ↓
[EditorViewModel] AudioSelectCommand.ExecuteAsync()
  ↓
[IAudioService.LoadAudioAsync(filePath)]
  ├─ Copy file to AppData
  ├─ Extract duration, metadata
  └─ Initialize NAudio player
  ↓
[EditorViewModel.CurrentAudio = metadata]
  ↓
[View updates] Text shows "Duration: 3:45"
  ↓
[Timer] Every 100ms:
  ├─ Get FFT data: IAudioService.GetFFTData()
  ├─ Trigger VisualizerControl redraw
  ├─ IVisualizerService.RenderSpectrum()
  └─ Canvas displays bars
```

---

## Concurrency & Threading

### Main Thread (UI Thread)
- **Runs:** WPF message loop, XAML rendering, ViewModel property changes
- **Don't:** Long I/O, FFT calculations

### Background Threads
- **Audio Playback:** NAudio internally uses background threads
- **FFT Extraction:** Can run on background (called frequently)
- **Rendering:** `Task.Run(() => { RenderAsync() })` runs on ThreadPool
- **Progress Reporting:** Via `IProgress<T>` (marshaled back to UI thread)

### Synchronization
```
[UI Thread] → ViewModel.RenderCommand.ExecuteAsync()
             ↓
             await Task.Run(async () => { 
                 await _ffmpegService.RenderAsync(..., progress) 
             })
             ↓
[ThreadPool] → FFmpeg rendering
             ├─ SkiaSharp frame generation
             └─ Pipe to FFmpeg stdin
             ↓
             IProgress<RenderProgress>.Report()
             ↓
[UI Thread] → ViewModel.RenderProgress = update.Percent
             ↓
[View Binding] → ProgressBar.Value = RenderProgress
```

---

## Error Handling & Logging

### Error Flow
```
User action (e.g., Render)
  ↓
ViewModel.Command → Service call
  ↓
[Service throws exception]
  ↓
[Try/Catch in ViewModel or Service]
  ├─ Log via Serilog
  └─ Wrap in custom exception
  ↓
[ViewModel catches]
  ├─ Set ErrorMessage property (bound to View)
  └─ Show user-friendly message
  ↓
[View displays error in MessageBox or inline]
```

### Logging
- **Info:** Normal operations (audio loaded, render started)
- **Warning:** Recoverable issues (missing FFmpeg, API slow)
- **Error:** Failures (file corrupt, render crashed)
- **Debug:** Low-level details (FFT values, buffer info)

---

## External Service Integration

### FFmpeg (Required)
- **Location:** System PATH or user-configured path
- **Integration:** Process.Start() + StdOut/StdErr for commands
- **Streaming:** Pipe SkiaSharp frames to FFmpeg stdin (for visualizer rendering)

### Yescale API (Phase 4)
- **HTTP:** HttpClient + JSON
- **Authentication:** API key in Settings
- **Cache:** Store results locally (avoid redundant calls)

### Image Search APIs (Phase 4)
- **Unsplash / Pexels / Pixabay:** HTTP GET with API key
- **Caching:** Save downloaded images to AppData
- **Rate Limiting:** Implement throttle queue

---

## Performance Optimization Strategies

| Component | Bottleneck | Optimization |
|-----------|-----------|--------------|
| **Visualizer** | FFT + Drawing | Cache FFT results, use InvalidateVisual() wisely |
| **Canvas Rendering** | Drag&drop redraw | Use VirtualizingCanvas for large element counts |
| **Database Queries** | Large projects | Index on ProjectId, SegmentId; eager load relations |
| **FFmpeg Render** | CPU/GPU | Use GPU acceleration (NVENC/QSV); limit concurrent renders |
| **Audio Loading** | Memory | Stream large files, don't load into memory |
| **UI Responsiveness** | Main thread | All async operations off UI thread |

---

## Testing Architecture (Phase 2+)

```
Unit Tests (xUnit):
  ├─ Services/ → ServiceName.Tests.cs (mock dependencies)
  ├─ Models/ → ModelName.Tests.cs (validation, serialization)
  └─ Utilities/ → Utility.Tests.cs (helpers, extensions)

Integration Tests:
  ├─ Database → Test EF context, migrations
  ├─ FFmpeg → Test render pipeline with sample files
  └─ API → Mock Yescale/Unsplash responses

UI Tests (Manual + WinAppDriver):
  ├─ Main workflow: Load audio → Edit → Render
  └─ Edge cases: Invalid files, missing FFmpeg, etc.
```

---

## Deployment & Distribution (Phase 6)

```
Development:
  └─ Visual Studio (F5 Debug)

Release Build:
  ├─ dotnet publish -c Release
  ├─ Self-contained executable
  └─ Single .exe (no .NET required on user machine)

Installer (Optional):
  └─ NSIS or WiX → MSI → Windows Add/Remove Programs

Distribution:
  ├─ Direct download (.exe or .msi)
  ├─ GitHub Releases
  └─ Update checker (download new version silently)
```

---

## Scalability Notes (Future)

### v1.x (Current)
- Single render at a time
- Local storage only
- 1 project open at a time

### v2.0 (Future)
- Backend API for concurrent rendering
- Cloud storage (Google Drive, OneDrive)
- Multi-project workspace
- Collaborative editing

---

## Key Invariants & Constraints

1. **No hardcoded paths** → Use config + AppData
2. **No blocking calls** → async/await everywhere
3. **Dispose resources** → using statements, IAsyncDisposable
4. **Validate input early** → Fail-fast on bad data
5. **Log errors** → Never swallow exceptions silently
6. **Thread-safe collections** → ObservableCollection on UI thread only
7. **Test render quality** → Compare output frame-by-frame

---

Last updated: 2026-02-06

See also:
- `decisions.md` - ADR decisions behind architecture
- `code_rules.md` - Implementation guidelines
- `state.md` - Phase timeline
