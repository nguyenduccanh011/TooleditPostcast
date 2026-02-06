# Code Standards & Rules

## Project: Podcast Video Editor
**Effective from:** 2026-02-06

---

## 1. Language & Framework

### Target
- **Language:** C# 12.0 (.NET 8)
- **UI Framework:** WPF (Windows Presentation Foundation)
- **Pattern:** MVVM (Model-View-ViewModel)

### .NET Conventions
- Use modern C# features: nullable reference types (`#nullable enable`), records, pattern matching
- Async/await everywhere for I/O operations (No `.Result` or `.Wait()`)
- Use `System.IO.Abstractions` or equivalent for testability (future phases)

---

## 2. Naming Conventions

### Classes & Interfaces
```csharp
public class AudioService          // PascalCase
public interface IAudioService     // IPascalCase for interfaces
public record AudioMetadata()      // Records also PascalCase
```

### Methods & Properties
```csharp
public async Task PlayAsync()      // PascalCase, async methods end with "Async"
public decimal Volume { get; set; } // PascalCase
private int _bufferSize;            // Private fields: _camelCase

// Bad:
public void play() { }             // ❌ Don't use camelCase for methods
public int volume;                 // ❌ Public fields, use properties
```

### Constants & Enums
```csharp
private const int DEFAULT_SAMPLE_RATE = 44100; // SCREAMING_SNAKE_CASE
public enum RenderQuality { Low, Medium, High } // PascalCase
```

### Events & Delegates
```csharp
public event EventHandler<AudioProgressEventArgs> ProgressChanged; // PascalCase
public delegate void RenderCompleted(string outputPath); // PascalCase
```

---

## 3. File Organization

### Folder Structure (Strict)
```
Core/
  ├── Models/
  │   ├── Project.cs
  │   ├── Segment.cs
  │   ├── AudioMetadata.cs
  │   └── ...
  ├── Services/
  │   ├── IAudioService.cs
  │   ├── AudioService.cs
  │   ├── FFmpegService.cs
  │   └── ...
  ├── Database/
  │   ├── AppDbContext.cs
  │   ├── Repositories/
  │   │   ├── IProjectRepository.cs
  │   │   └── ProjectRepository.cs
  │   └── Migrations/
  ├── Utilities/
  │   ├── Extensions.cs
  │   └── Constants.cs

Ui/
  ├── Views/
  │   ├── MainWindow.xaml (.xaml.cs)
  │   ├── EditorView.xaml (.xaml.cs)
  │   └── ...
  ├── ViewModels/
  │   ├── MainViewModel.cs
  │   ├── EditorViewModel.cs
  │   └── ...
  ├── Controls/ (Custom User Controls)
  │   ├── AudioPlayerControl.xaml
  │   ├── VisualizerControl.xaml
  │   └── ...
  ├── Resources/
  │   ├── Styles.xaml
  │   └── Themes/
  ├── Behaviors/ (Attached Behaviors)
  └── App.xaml
```

### File Naming
- **One class per file** (with private exceptions like `Exceptions.cs`)
- Filename **must match** class name: `AudioService.cs` contains `class AudioService`
- XAML: `ViewName.xaml` + `ViewName.xaml.cs`
- ViewModel: `ViewNameViewModel.cs`

---

## 4. Async & Threading Rules

### ✅ DO
```csharp
// Good: Async all the way
public async Task<Project> LoadProjectAsync(string id)
{
    return await _repository.GetProjectAsync(id);
}

// Good: ConfigureAwait for libraries (not UI code)
public async Task<Stream> ReadFileAsync(string path)
{
    return await File.OpenReadAsync(path).ConfigureAwait(false);
}

// Good: Use IProgress for background work
public async Task RenderAsync(RenderConfig config, IProgress<RenderProgress> progress)
{
    await Task.Run(() => {
        // Work here
        progress.Report(new RenderProgress { Percent = 50 });
    });
}
```

### ❌ DON'T
```csharp
// Bad: Blocking calls
public Project LoadProject(string id)
{
    return _repository.GetProjectAsync(id).Result; // ❌ DEADLOCK RISK
}

// Bad: No ConfigureAwait in library code
public async Task SaveAsync()
{
    await File.WriteAsync(path, data); // ❌ May capture UI context unnecessarily
}

// Bad: Fire and forget
_ = SomeAsyncMethod(); // ❌ Except in very specific cases (unit tests, events)
```

---

## 5. Logging Standards

### Serilog Usage
```csharp
using Serilog;

public class AudioService
{
    private readonly ILogger _logger = Log.ForContext<AudioService>();

    public async Task<AudioMetadata> LoadAudioAsync(string filePath)
    {
        _logger.Information("Loading audio from {FilePath}", filePath);
        try
        {
            var metadata = await _waveFile.ReadMetadataAsync(filePath);
            _logger.Information("Audio loaded: Duration={Duration}ms, SampleRate={SampleRate}Hz", 
                metadata.Duration.TotalMilliseconds, metadata.SampleRate);
            return metadata;
        }
        catch (FileNotFoundException ex)
        {
            _logger.Error(ex, "Audio file not found: {FilePath}", filePath);
            throw;
        }
    }
}
```

### Log Levels
- **Information**: Normal operations (file loaded, render started)
- **Warning**: Recoverable issues (file format not ideal, API slow)
- **Error**: Errors that prevent operation (file corrupt, API fail, retry exhausted)
- **Debug**: Low-level details (FFT data, buffer updates) - only during dev

### ❌ Don't Use
- `Console.WriteLine()` - use Serilog
- `MessageBox` for logging - use Serilog + UI notification separately

---

## 6. Exception Handling

### Pattern
```csharp
public async Task<Project> LoadProjectAsync(string id)
{
    if (string.IsNullOrEmpty(id))
        throw new ArgumentNullException(nameof(id));

    try
    {
        return await _repository.GetProjectAsync(id);
    }
    catch (DbUpdateException ex)
    {
        _logger.Error(ex, "Database error loading project {Id}", id);
        throw new ApplicationException("Failed to load project", ex);
    }
    catch (FileNotFoundException ex)
    {
        _logger.Error(ex, "Project file missing: {Id}", id);
        throw new ApplicationException("Project data file not found", ex);
    }
}
```

### Custom Exceptions (Create in `Utilities/Exceptions.cs`)
```csharp
public class RenderException : Exception { }
public class AudioProcessingException : Exception { }
public class FFmpegException : Exception { }
```

### Rules
- Validate input early (fail-fast)
- Catch specific exceptions, not `Exception`
- Log before re-throwing or wrapping
- Never swallow exceptions silently

---

## 7. Memory Management

### Disposal Pattern (C# 8+)
```csharp
// Good: For classes that use unmanaged resources
public class AudioService : IAsyncDisposable
{
    private WaveInEvent _waveIn;

    public async ValueTask DisposeAsync()
    {
        _waveIn?.Dispose();
        await Task.CompletedTask;
    }
}

// Usage:
await using (var service = new AudioService())
{
    await service.PlayAsync();
}

// Good: For UI controls / Bitmaps
using (var bitmap = SKBitmap.Decode(imagePath))
{
    // Use bitmap
} // Automatically disposed
```

### Image/Bitmap Handling (Critical for SkiaSharp)
```csharp
// ❌ BAD: Memory leak
public SKBitmap LoadImage(string path)
{
    return SKBitmap.Decode(path); // ❌ Caller must dispose!
}

// ✅ GOOD: Caller knows they must dispose
public SKBitmap LoadImage(string path) => SKBitmap.Decode(path);
// OR use in `using`:
using (var bitmap = SKBitmap.Decode(path)) { ... }

// ✅ GOOD: Alternative - return Stream or use Stream
public Stream GetImageStream(string path) => File.OpenRead(path);
```

---

## 8. MVVM ViewModel Pattern

### Template
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public partial class EditorViewModel : ObservableObject
{
    private readonly IAudioService _audioService;

    // Observable properties (auto-notify)
    [ObservableProperty]
    private Project currentProject;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private TimeSpan currentPosition;

    public EditorViewModel(IAudioService audioService)
    {
        _audioService = audioService;
    }

    // Commands
    [RelayCommand]
    public async Task PlayAsync()
    {
        IsPlaying = true;
        await _audioService.PlayAsync();
    }

    [RelayCommand]
    public async Task PauseAsync()
    {
        IsPlaying = false;
        await _audioService.PauseAsync();
    }

    // Business logic
    private void UpdatePosition(TimeSpan position)
    {
        CurrentPosition = position;
    }
}
```

### XAML Binding
```xaml
<Button Command="{Binding PlayCommand}" Content="Play" />
<TextBlock Text="{Binding CurrentPosition, StringFormat=mm\\:ss}" />
<ProgressBar Value="{Binding PlaybackProgress, Mode=OneWay}" />
```

---

## 9. Entity Framework & Database

### DbContext Usage
```csharp
public class AppDbContext : DbContext
{
    public DbSet<Project> Projects { get; set; }
    public DbSet<Segment> Segments { get; set; }
    public DbSet<Element> Elements { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=app.db");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Fluent API configuration
        builder.Entity<Project>()
            .HasMany(p => p.Segments)
            .WithOne(s => s.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

### Repository Pattern (Keep simple in Phase 1)
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

    public async Task CreateAsync(Project project)
    {
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();
    }
}
```

---

## 10. Testing Standards (Phase 2+)

### Unit Test Pattern (xUnit)
```csharp
public class AudioServiceTests
{
    [Fact]
    public async Task LoadAudioAsync_ValidFile_ReturnsMetadata()
    {
        // Arrange
        var service = new AudioService();
        var testFile = Path.Combine(AppContext.BaseDirectory, "test_audio.wav");

        // Act
        var result = await service.LoadAudioAsync(testFile);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Duration.TotalSeconds > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task LoadAudioAsync_InvalidPath_ThrowsException(string filePath)
    {
        // Arrange
        var service = new AudioService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LoadAudioAsync(filePath)
        );
    }
}
```

---

## 11. Configuration & Secrets

### appsettings.json
```json
{
  "App": {
    "Version": "1.0.0",
    "AppDataPath": "%APPDATA%\\PodcastVideoEditor"
  },
  "FFmpeg": {
    "Path": "ffmpeg.exe",
    "UseGpu": false
  },
  "Database": {
    "Path": "app.db"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Secrets (Do NOT commit to repo)
- API keys (Yescale, Unsplash) → Store in user settings (LocalAppData)
- Database password (if any) → Store separately

---

## 12. Code Review Checklist

Before merging any PR or commit:

- [ ] Async/await used correctly (no `.Result`, proper `ConfigureAwait`)
- [ ] No hardcoded paths (use config or constants)
- [ ] Logging statements added for important operations
- [ ] No memory leaks (Dispose pattern used for unmanaged resources)
- [ ] Null checks / input validation
- [ ] Unit tests added or updated
- [ ] Code compiles without warnings
- [ ] Naming conventions followed
- [ ] No commented-out code left
- [ ] No Console.WriteLine (use Serilog)

---

## 13. Performance Considerations

### Critical Path (Optimize)
1. **FFT Data Extraction** → Keep O(n log n), cache if possible
2. **UI Render Loop** → Use InvalidateVisual strategically
3. **File I/O** → Async + buffer streaming
4. **Memory** → Dispose bitmaps immediately, don't keep in collections

### Benchmarking (Phase 2+)
- Use BenchmarkDotNet for critical methods
- Profile with dotTrace for memory leaks
- Monitor RAM during long renders (24h+ tests)

---

## 14. Commit Message Format

### Template
```
[TAG] Brief description (50 chars max)

Detailed explanation (70 chars per line).
Multiple paragraphs allowed.

Fixes: #123
Related: #456
```

### Tags
```
[FEAT]   - New feature
[FIX]    - Bug fix
[REFACTOR] - Code refactoring
[DOCS]   - Documentation
[TEST]   - Test additions
[PERF]   - Performance improvement
[CI/CD]  - Build/deployment changes
```

### Example
```
[FEAT] Implement audio visualization with SkiaSharp

- Added VisualizerService with FFT processing
- Created VisualizerControl (WPF/Skia)
- Hooked into AudioService.PositionChanged event
- Performance: 60 FPS at 1080p with 64 bars

Fixes: #45
```

---

## 15. Documentation Requirements

### Code Comments
```csharp
// ✅ Good: Explain WHY, not WHAT
public async Task RenderAsync(RenderConfig config)
{
    // Use MediaFoundation API instead of FFmpeg directly for GPU acceleration
    // This reduces render time by ~40% on NVIDIA GPUs
    var gpuRenderer = new MFVideoRenderer();
    ...
}

// ❌ Bad: Comments state the obvious
public void Play()
{
    // Play the audio
    player.Play();
}
```

### XML Documentation (Public API)
```csharp
/// <summary>
/// Loads an audio file and extracts metadata.
/// </summary>
/// <param name="filePath">Full path to audio file (.mp3, .wav, .m4a)</param>
/// <returns>AudioMetadata containing duration, sample rate, channels</returns>
/// <exception cref="FileNotFoundException">If file does not exist</exception>
/// <exception cref="AudioProcessingException">If audio format unsupported</exception>
public async Task<AudioMetadata> LoadAudioAsync(string filePath) { ... }
```

---

## 16. Known Anti-Patterns (DON'T)

| Anti-Pattern | Problem | Alternative |
|--------------|---------|-------------|
| `.Result` / `.Wait()` | Deadlock risk | Use `await` |
| Global variables | State management nightmare | Dependency injection |
| Nested callbacks | Callback hell | Async/await |
| Try/catch without re-throwing | Silent failures | Log then re-throw |
| `foreach` with index | Error-prone | `for` loop or LINQ |
| `string` concatenation in SQL | SQL injection | Parameterized queries (EF Core) |
| No null checks | NullReferenceException | Use `?.` or null validation |

---

## References

- [C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- [MVVM Toolkit Documentation](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)
- [Entity Framework Core Best Practices](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/)
- [Async/Await Best Practices](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
- [SkiaSharp Documentation](https://learn.microsoft.com/en-us/xamarin/xamarin-forms/user-interface/graphics/skiasharp/)

---

Last updated: 2026-02-06
Review interval: Monthly or when adding new modules
