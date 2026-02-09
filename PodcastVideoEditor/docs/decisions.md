# Architecture Decision Records (ADR)

**Project:** Podcast Video Editor  
**Format:** Lightweight ADR (Problem, Decision, Rationale, Alternatives)

---

## ADR-001: WPF for Desktop UI (vs. WinUI 3 / Avalonia)

**Date:** 2026-02-06

### Problem
Need to choose UI framework for desktop application on Windows. Requirements: Drag&Drop support, custom drawing (Visualizer), high performance, mature ecosystem.

### Decision
**Use WPF (.NET Framework 8 / Windows Presentation Foundation)**

### Rationale
1. **Mature Ecosystem:** WPF has 15+ years of production use, extensive libraries (GongSolutions.WPF.DragDrop)
2. **Custom Drawing:** Native support for shapes, Canvas, and integration with SkiaSharp for advanced graphics
3. **MVVM Built-in:** First-class support for binding and MVVM pattern
4. **Hardware Acceleration:** GPU rendering support (D3D11 backend)
5. **Cross-version Stability:** Works consistently across .NET 6/7/8
6. **Performance:** Suitable for real-time visualizer (60 FPS achievable)

### Alternatives Considered
- **WinUI 3:** Modern but less mature, still has bugs, fewer third-party libraries for drag&drop
- **Avalonia:** Cross-platform but adds complexity, slower render performance for our use case
- **Electron/Tauri:** Overkill for desktop tool, higher resource consumption

### Trade-offs
- ❌ Windows-only (acceptable per requirements: "Desktop" = Windows)
- ❌ Steeper learning curve than WinUI
- ✅ Proven reliability, extensive community support

### Status: ACCEPTED

---

## ADR-002: SkiaSharp for Visualizer Rendering (vs. GDI+ / Direct2D)

**Date:** 2026-02-06

### Problem
Need high-performance 2D graphics library for real-time visualizer (spectrum bars, waveform). Must support both UI preview and offline frame rendering for FFmpeg.

### Decision
**Use SkiaSharp** (Google's Skia engine, C# bindings)

### Rationale
1. **Cross-Platform Render Path:** Can vẽ to screen (WPF) or to Bitmap (for FFmpeg pipe)
2. **Performance:** Optimized for graphics rendering, GPU acceleration, ~40% faster than System.Drawing
3. **Feature-Rich:** Supports gradients, blur, transformations, etc.
4. **Offline Rendering:** Can render frames without window/UI context (critical for server-side rendering later)
5. **Active Development:** Maintained by Google, regular updates

### Alternatives Considered
- **System.Drawing / GDI+:** Legacy, slower, limited features, deprecated
- **Direct2D:** Windows-only, COM interop complexity, harder to use from C#
- **OpenGL (via SharpGL):** Powerful but overkill, steeper learning curve, licensing concerns

### Trade-offs
- ❌ Additional NuGet dependency
- ❌ Learning curve (Canvas API)
- ✅ Superior performance, future-proof

### Status: ACCEPTED

---

## ADR-003: FFmpeg via Process + Pipe (vs. Xabe.FFmpeg high-level / libffmpeg.NET)

**Date:** 2026-02-06

### Problem
Need to integrate FFmpeg for video rendering. Options range from high-level wrapper to direct binary execution.

### Decision
**Use hybrid approach:**
- **Xabe.FFmpeg** for common operations (audio mixing, basic concatenation)
- **Direct Process.Start()** for complex filter graphs (FFmpeg `-filter_complex`)
- **Pipe-based streaming** to send SkiaSharp-rendered frames to FFmpeg stdin

### Rationale
1. **Complex Filter Graphs:** Xabe.FFmpeg doesn't expose full `-filter_complex` syntax needed for concat+visualizer+overlay
2. **Streaming:** Piping frames to FFmpeg stdin avoids writing temporary files, saves I/O
3. **Control:** Direct Process allows maximum flexibility for optimization (GPU acceleration, threading)
4. **Reliability:** FFmpeg is battle-tested, used by YouTube/Facebook

### Alternatives Considered
- **Only Xabe.FFmpeg:** Limited to simple operations, can't pipe custom frames
- **libffmpeg.NET (P/Invoke):** Complex, version-dependent, licensing issues
- **Embedded FFmpeg library:** Bloats binary, licensing complications

### Trade-offs
- ❌ Complex FFmpeg command building (mitigate: create FFmpegCommandBuilder class)
- ❌ Requires FFmpeg binary on user's system (acceptable: mentioned in setup)
- ✅ Maximum flexibility, performance, control

### Status: ACCEPTED

---

## ADR-004: SQLite for Project/Segment Storage (vs. JSON / In-Memory)

**Date:** 2026-02-06

### Problem
Need persistent storage for Projects, Segments, Elements, Assets. Requirements: quick queries, support for large datasets, offline-first.

### Decision
**Use SQLite + Entity Framework Core**

### Rationale
1. **Structured Data:** Project relationships (1:N Segments, Elements, Assets) suit relational model
2. **Query Performance:** Can query segments by time range, filter elements by type efficiently
3. **ACID Transactions:** Data integrity if app crashes during save
4. **No Server:** Serverless, file-based, zero setup
5. **Entity Framework:** ORM abstracts DB layer, easy to swap DB later (PostgreSQL for v2)
6. **Scalability:** Can handle projects with thousands of segments smoothly

### Alternatives Considered
- **JSON file:** Simple but slow for large projects, no validation, no transactions
- **In-memory + export:** Lose data on crash, not suitable for long editing sessions
- **SQL Server / PostgreSQL:** Overkill for v1, adds deployment complexity

### Trade-offs
- ❌ Database versioning/migrations needed (mitigate: EF Core migrations)
- ✅ Future-proof (can migrate to server DB seamlessly)
- ✅ Fast, reliable, proven

### Status: ACCEPTED

---

## ADR-005: NAudio for Audio I/O (vs. Windows.Media.Audio / BASS)

**Date:** 2026-02-06

### Problem
Need to: Load audio files, get FFT data for visualizer, play/pause/seek, mix multiple audio tracks (BGM).

### Decision
**Use NAudio 2.x** (open-source, comprehensive audio library for .NET)

### Rationale
1. **FFT Support:** Provides FFT analysis directly (critical for visualizer)
2. **Multi-track Support:** WaveFileSampleProvider allows mixing multiple sources
3. **Format Support:** Handles MP3, WAV, FLAC via codecs
4. **Active Community:** Maintained, updated regularly, extensive examples
5. **Free/Open-Source:** No licensing fees, can inspect code

### Alternatives Considered
- **Windows.Media.Audio:** Modern but lacks FFT, limited control
- **BASS (unmanaged library):** Faster but requires DLL, proprietary, licensing needed
- **CSCore:** Similar to NAudio, less maturity

### Trade-offs
- ❌ Additional dependency
- ✅ Perfect fit for requirements
- ✅ Free and open

### Status: ACCEPTED

---

## ADR-006: Async/Await Throughout (vs. Thread Pools / Reactive Extensions)

**Date:** 2026-02-06

### Problem
App needs to handle long-running I/O (FFmpeg render, audio loading) without blocking UI.

### Decision
**Use async/await for all I/O operations**

### Rationale
1. **Simplicity:** Easier to reason about than Rx or ThreadPool callbacks
2. **UI Thread Safety:** Automatic context capture prevents deadlocks
3. **Cancellation:** Built-in CancellationToken support
4. **Modern Standard:** C# 5.0+, battle-tested across industry
5. **UI Responsiveness:** UI stays responsive without explicit threading

### Alternatives Considered
- **ThreadPool.QueueUserWorkItem:** Low-level, error-prone
- **Reactive Extensions (Rx):** Overkill for this project, steep learning curve
- **BackgroundWorker:** Legacy pattern, verbose

### Trade-offs
- ❌ Requires discipline (no `.Result`, no `.Wait()`)
- ✅ Cleaner, safer, more performant

### Status: ACCEPTED

---

## ADR-007: MVVM Toolkit (CommunityToolkit.Mvvm) for Binding (vs. Caliburn Micro / Prism)

**Date:** 2026-02-06

### Problem
Need MVVM framework for data binding between UI and ViewModel. Options: full frameworks vs. lightweight.

### Decision
**Use CommunityToolkit.Mvvm (lightweight, source-generator based)**

### Rationale
1. **Lightweight:** Only ~20KB, minimal overhead
2. **Source Generators:** Auto-generates INotifyPropertyChanged code at compile-time (zero runtime cost)
3. **Attributes-based:** Simple `[ObservableProperty]` syntax, less boilerplate
4. **No Service Locator:** Encourages dependency injection
5. **Official:** Maintained by Microsoft

### Alternatives Considered
- **Caliburn Micro:** More opinionated, more features than we need
- **Prism:** Enterprise-grade but heavy for desktop tool
- **Simple INotifyPropertyChanged:** Too much manual code

### Trade-offs
- ❌ Requires .NET 7+ (we're using .NET 8, so OK)
- ✅ Minimal dependencies, excellent for our use case

### Status: ACCEPTED

---

## ADR-008: Phase-Based Delivery (Gaia Đoạn) vs. Big-Bang Release

**Date:** 2026-02-06

### Problem
6 major phases spanning 16 weeks. Risk of feature creep, untested accumulation.

### Decision
**Deliver incrementally, phase-by-phase. Each phase must be runnable.**

### Rationale
1. **Risk Reduction:** Catch architectural issues early
2. **Feedback:** Get user feedback after each phase
3. **Scope Control:** Easier to defer features to next phase
4. **Testing:** Each phase can be thoroughly tested before moving on
5. **Morale:** Visible progress keeps team motivated

### Implementation
- Phase 1 end: Basic audio upload + render = MVP
- Phase 2 end: Canvas editor + visualizer working
- Phase 3 end: Full timeline + sync
- etc.

### Trade-offs
- ❌ More frequent integrations
- ✅ Lower risk, better quality

### Status: ACCEPTED

---

## ADR-009: Local Render (Phase 1-5) vs. Backend Render Service (Phase 2)

**Date:** 2026-02-06

### Problem
Rendering video is computationally expensive. Where should it run: on user's machine or backend server?

### Decision
**Phase 1-5: Local render (user's machine)**  
**Phase 2 (future): Offer optional backend service**

### Rationale (v1)
1. **Privacy:** User data never leaves their machine
2. **No Infrastructure:** No server cost, no uptime SLA
3. **Works Offline:** No internet required
4. **Simpler Architecture:** Just app + FFmpeg

### Future Decision (Phase 2)
- Add optional API endpoint to submit render jobs
- Backend processes video, returns download link
- User can choose: local or cloud

### Trade-offs (Phase 1)
- ❌ Slower render on weak machines
- ✅ Privacy, simplicity, offline capability

### Status: ACCEPTED (v1)  
Deferred to Phase 2: Backend service

---

## ADR-010: API Keys in Client (No Backend Token Gateway)

**Date:** 2026-02-06

### Problem
Yescale API (AI segmentation) and Unsplash API (image search) require authentication. Should API keys be stored on client or proxied through backend?

### Decision
**Phase 1: Client stores API keys in LocalAppData (encrypted via DPAPI)**

### Rationale
1. **Simplicity:** No backend infrastructure required
2. **Cost:** No bandwidth cost for token proxying
3. **Security:** DPAPI encrypts keys at rest (Windows security)
4. **Offline:** APIs are optional, can work without them
5. **Flexibility:** User can use their own API key

### Security Measures
```csharp
// Encrypt API key before storing
var encrypted = ProtectedData.Protect(
    Encoding.UTF8.GetBytes(apiKey),
    null,
    DataProtectionScope.CurrentUser
);

// Decrypt when needed
var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
```

### Future Decision (Phase 2)
- If scaling beyond personal use, add backend gateway
- Backend validates request, adds API key, forwards to 3rd party
- Prevents API key exposure in network traffic

### Trade-offs
- ❌ API keys visible to power users (reversible with effort)
- ✅ Simple, no backend needed, user control

### Status: ACCEPTED

---

## ADR-011: Yescale (Custom) vs. OpenAI for AI Segmentation

**Date:** 2026-02-06

### Problem
Script segmentation (breaking podcast into logical chunks) needs AI. Options: OpenAI (ChatGPT), Gemini, or custom Yescale service.

### Decision
**Use Yescale API** (user's preference, assumption: cost-effective)

### Rationale
1. **User Preference:** Explicitly requested
2. **Cost:** Assumed cheaper than OpenAI
3. **Latency:** Likely faster (less latency)
4. **Custom Model:** Potentially fine-tuned for podcast content

### Integration Notes
- Yescale endpoint: [To be provided]
- API format: HTTP POST JSON, returns Segment[] { start, end, text }
- Fallback: Manual script entry if API fails (required for MVP)

### Alternatives (v1.1)
- Allow user to choose: OpenAI vs. Yescale vs. Custom endpoint
- Each has pros/cons, defer to phase 4

### Status: ACCEPTED (v1)

---

## ADR-012: Render Progress Reporting (Polling vs. Events)

**Date:** 2026-02-06

### Problem
Long-running render process needs to report progress (%, current step). How to communicate updates?

---

## ADR-013: Timeline Implementation - Reference vs Build-From-Scratch

**Date:** 2026-02-06

### Problem
Timeline control is the hardest WPF component to build from scratch. Risk: 2-3 months of debugging UI lag, zoom bugs, scroll issues, drag-drop glitches.

### Decision
**Use existing open-source code as reference, NOT build from zero.**

Do NOT simply copy-paste entire projects (too many dependencies). Instead:
- Study architecture patterns from Gemini, nGantt, Smeedee
- Copy formula/algorithms (PixelsPerSecond, TimeToPosition, etc.)
- Copy XAML templates and styles
- Copy ViewModel structure

### Rationale
1. **Proven patterns:** These projects have battle-tested solutions
2. **Performance:** Virtualization & optimization already solved
3. **Time efficiency:** Save 2-3 months of debugging
4. **Lower risk:** Avoid subtle bugs (giật lag, zoom inaccuracy, scroll desync)

### Reference Projects
See `docs/reference-sources.md` for:
- **Gemini.Modules.Timeline** - MVVM architecture ⭐
- **nGantt** - Drag/Resize/Collision patterns ⭐
- **Smeedee** - Virtualization for performance ⭐
- **NAudio WPF Samples** - Audio visualization

### Timeline Implementation Protocol (Phase 3)
When reaching Phase 3 (Script & Timeline):
1. Read `docs/reference-sources.md` completely
2. Clone/explore reference repositories
3. Study specific files/patterns mentioned
4. Extract formulas and patterns (NOT entire code)
5. Build Timeline using MVVM pattern + reference learnings

### Trade-offs
- ❌ "Reinventing the wheel" - No, we reference proven solutions
- ✅ Quality control - Proven patterns from multiple sources
- ✅ Time savings - Months of saved debugging
- ✅ Maintainability - Understood architecture before building

### Status: ACCEPTED

When resuming Phase 3, start with `docs/reference-sources.md` BEFORE writing Timeline code.

### Decision
**Use IProgress<T> event model** (async pattern)

### Rationale
```csharp
// Clean, decoupled
public async Task RenderAsync(RenderConfig config, IProgress<RenderProgress> progress)
{
    for (int frame = 0; frame < totalFrames; frame++)
    {
        progress?.Report(new RenderProgress { 
            PercentComplete = (frame / totalFrames) * 100,
            CurrentStep = "Rendering frames"
        });
    }
}

// ViewModel subscribes
var progress = new Progress<RenderProgress>(update =>
{
    ProgressPercent = update.PercentComplete;
    StatusText = update.CurrentStep;
});

await _renderService.RenderAsync(config, progress);
```

### Alternatives Considered
- **Events:** More verbose, unclear ownership
- **Polling:** Inefficient, race conditions
- **Callbacks:** Less clean than IProgress

### Trade-offs
- ✅ Clean, decoupled, thread-safe
- ✅ Cancellation token support built-in

### Status: ACCEPTED

---

## Summary of Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| UI Framework | WPF | Mature, drag&drop support, hardware acceleration |
| Graphics | SkiaSharp | High-perf, cross-platform render path |
| Video Encoding | FFmpeg | Industry standard, maximum control |
| Database | SQLite + EF | Structured data, ACID, no server |
| Audio I/O | NAudio | FFT support, multi-track, open-source |
| Async Model | async/await | Simple, safe, standard |
| MVVM Framework | Toolkit.Mvvm | Lightweight, source-generated |
| Delivery | Phased | Incremental, reduce risk |
| Render Location | Local (v1) | Privacy, offline, simplicity |
| API Keys | Client (encrypted) | Simple, DPAPI protection |
| AI Provider | Yescale | User preference, cost-effective |
| Progress | IProgress<T> | Clean, thread-safe |

---

## Forward References: Phase 3 / 5 / 6 (2026-02-08)

Các quyết định và nhiệm vụ sau đã được ghi chi tiết trong **`docs/issues.md`**. Khi lên kế hoạch hoặc thực hiện từng phase, cần đọc và đưa vào TP/ST.

| Phase | Issue | Tóm tắt |
|-------|-------|--------|
| Phase 3 | #13 | Audio track tích hợp vào timeline (waveform/track, CapCut/Premiere style). |
| Phase 3 | #12 | UI Editor gọn đẹp — ưu tiên Phase 6; Phase 3 chỉ chỉnh nhỏ nếu cần. |
| Phase 5 | #10 | Output path: chọn thư mục xuất, nút Mở file/thư mục, (tùy chọn) danh sách renders. |
| Phase 5 | #11 | Render từ Canvas (bỏ ảnh tĩnh); output = frame từ Canvas/segment. |
| Phase 6 | #12 | UI Editor tab — tối ưu gọn đẹp giống CapCut. |

**Nguồn chi tiết:** `docs/issues.md` (Issue #10–#13). **Bảng Phase Commitments:** `docs/state.md`.

---

Last updated: 2026-02-08

Next review: At end of Phase 1 (2026-02-27)

Request change if assumptions change: Update issue in `docs/issues.md` or create discussion.
