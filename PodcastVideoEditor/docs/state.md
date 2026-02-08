# Project State - Podcast Video Editor

## Metadata
- **Project Name:** PodcastVideoEditor (PVE)
- **Created:** 2026-02-06
- **Tech Stack:** C# .NET 8 + WPF + FFmpeg + SkiaSharp
- **Target:** Desktop (Windows) - Mass Video Production Tool

---

## Objective & Vision

**Mục tiêu chính:**
Xây dựng công cụ chuyên dụng để tạo video từ podcast (audio + script) một cách tự động hóa, hỗ trợ Drag&Drop layout, AI auto-segmentation, auto image search, complex render với visualizer động.

**Độc lập cạnh tranh:**
- Sản xuất video hàng loạt (mass production) với render ổn định, mượt, hiệu suất cao
- Timeline sync chính xác, visualizer real-time
- Tích hợp API AI (Yescale) và API ảnh (Unsplash/Pexels/Pixabay)
- Template reusable, kéo thả trực quan

---

## Scope v1.0 (MVP - Minimum Viable Product)

### ✅ INCLUDED
- **Req #1-4**: Upload audio, preview, basic edit (đơn giản), render cơ bản
- **Req #4**: Visualizer spectrum real-time
- **Req #7**: Timeline nền + segment (ảnh tĩnh)
- **Req #8**: BGM mixing (volume, fade)
- **Req #9**: Kéo thả element (title, logo, script, visualizer, ảnh)
- **Req #10-11**: Preview + render MP4 (local)
- **Req #13**: Template Save/Load
- **Req #14**: Settings (API keys)

### ❌ OUT OF SCOPE (v1.0)
- Req #15 (Ken Burns) - defer to v1.1
- Complex timeline effects (blend modes, color grading) - v1.2
- Concurrent render (multi-job) - v2.0
- Auto image search (Req #6) - v1.1
- AI script segmentation (Req #5) - v1.1
- Backend API render - v2.0

---

## Architecture Decision

### Tech Stack
```
Backend/Core:      C# .NET 8 (Console Services)
UI Framework:      WPF + MVVM Toolkit
Graphics:          SkiaSharp (visualizer), System.Windows (UI controls)
Audio:             NAudio 2.x (play, FFT), NAudio.Extras
Render Engine:     FFmpeg (via Xabe.FFmpeg + Process)
Database:          SQLite + Entity Framework Core 8
HTTP Client:       HttpClientFactory
Drag&Drop:         GongSolutions.WPF.DragDrop
Logging:           Serilog + Serilog.Sinks.File
JSON Config:       System.Text.Json
```

### Architecture Pattern
- **MVVM**: ViewModel -> View binding via INotifyPropertyChanged
- **Service Layer**: Separate concerns (AudioService, FFmpegService, etc.)
- **Async/Await**: All I/O operations non-blocking
- **Background Tasks**: Task.Run cho heavy operations, report progress via IProgress<T>

---

## Phase Timeline (Giai Đoạn)

| Phase | Name | Status | Target |
|-------|------|--------|--------|
| **Phase 1** | Core Engine & Audio | ✅ DONE (100%) | Feb 6-7 |
| **Phase 2** | Canvas Editor & Visualizer | ✅ DONE (100%) | Feb 7 - Mar 7 |
| **Phase 3** | Script & Timeline | ⏳ TODO | Week 7-8 |
| **Phase 4** | AI & Automation | ⏳ TODO | Week 9-10 |
| **Phase 5** | Render Pipeline | ⏳ TODO | Week 11-13 |
| **Phase 6** | Polish & QA | ⏳ TODO | Week 14-15 |
| **Release** | v1.0 | ⏳ TODO | Week 16 |

---

## Current Phase: IDLE (Phase 2 closed)

**Status:** Phase 2 (Canvas Editor & Visualizer) đã đóng — ST-7 through ST-12 DONE. Sẵn sàng Phase 3 (Script & Timeline) khi bắt đầu TP mới.

**Last Updated:** 2026-02-08

---

## Dependencies & External Services

### Required Local Tools
- **FFmpeg**: User cài sẵn (path trong config)
- **.NET 8 SDK**: Dev machine
- **Visual Studio 2022 Community+**: IDE

### External APIs (v1.1+)
- **Yescale API**: AI script segmentation (opt-in via API key)
- **Unsplash/Pexels API**: Image search (opt-in via API key)

### NuGet Packages (Core)
```
MVVM:              CommunityToolkit.Mvvm 8.x
Database:          EntityFrameworkCore 8.x, SQLite
Audio:             NAudio 2.2.x, NAudio.Extras
Graphics:          SkiaSharp 2.88.x
FFmpeg:            Xabe.FFmpeg 7.x
HTTP:              Refit 7.x (for API client)
Logging:           Serilog 3.x, Serilog.Sinks.File
Drag&Drop:         GongSolutions.WPF.DragDrop 4.x
```

---

## Key Data Models

### Project Structure (JSON/DB)
```json
{
  "id": "proj_001",
  "name": "My First Video",
  "audioPath": "C:\\AppData\\..\\audio.mp3",
  "segments": [
    { "start": 0, "end": 10, "text": "Intro", "bgImage": "asset_123" }
  ],
  "elements": [
    { "type": "Title", "x": 100, "y": 50, "text": "My Title" }
  ],
  "bgmTracks": [
    { "path": "bgm.mp3", "volume": 0.3, "fadeIn": 2, "fadeOut": 2 }
  ],
  "renderSettings": {
    "resolution": "1080p",
    "aspectRatio": "9:16",
    "quality": "Medium"
  }
}
```

---

## Metrics & Success Criteria

| Criterion | Target | Status |
|-----------|--------|--------|
| Render speed (1080p 60s video) | <3 min | TBD |
| RAM usage (idle) | <200MB | TBD |
| RAM usage (render 4K) | <2GB | TBD |
| UI responsiveness (during render) | 60 FPS | TBD |
| Visualizer latency | <100ms | TBD |
| Startup time | <2s | TBD |

---

## Known Constraints & Risks

1. **SkiaSharp + FFmpeg Pipe**: Nếu SkiaSharp vẽ quá chậm, pipe tới FFmpeg sẽ chết. → Mitigate: Test FFT rendering early.
2. **Audio Sync**: Timestamp phải chính xác. → Mitigate: Extensive testing với sample files.
3. **Memory Leak**: Bitmap disposal nếu không careful. → Mitigate: Code review, profiling.

---

## Immediate Next Steps

- **Phase 2:** Đã đóng (ST-7–ST-12 done). Khi bắt đầu phiên mới: tạo TP-003 cho Phase 3 (Script & Timeline) trong `state.md` và `active.md`.
- **Phase 3 (Script & Timeline):** Chưa có TP — hỏi user "Vấn đề/feature tiếp theo?" hoặc tạo TP-003 theo scope Phase 3.

---

Last updated: 2026-02-08
