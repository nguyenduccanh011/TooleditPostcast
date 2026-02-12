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
| **Phase 3** | Script & Timeline | ✅ DONE (100%) | Week 7-8 |
| **Phase 4** | AI & Automation | ⏳ TODO | Week 9-10 |
| **Phase 5** | Render Pipeline | ⏳ TODO | Week 11-13 |
| **Phase 6** | Polish & QA | ⏳ TODO | Week 14-15 |
| **Release** | v1.0 | ⏳ TODO | Week 16 |

### Thứ tự TP so với các Phase khác

| Thứ tự | TP / Phase | Ghi chú |
|--------|------------|--------|
| 1 | Phase 1 (TP-001) | ✅ Done |
| 2 | Phase 2 (TP-002) | ✅ Done |
| 3 | Phase 3 (TP-003) | ✅ Done |
| 4 | Multi-track (TP-004) | ✅ Core done; ST-6/ST-7 hoãn. |
| 5 | **TP-005 MVP Visual & Preview** | **NEXT** — Gắn ảnh segment; Preview aspect + composite sync. |
| 6 | Phase 5 (Render Pipeline) | Sau TP-005 (issue #10, #11). |
| 7 | Phase 4 (AI) hoặc Phase 6 | Tùy ưu tiên. Phase 6: ST-6/ST-7, #12. |

**Khuyến nghị:** Làm **Multi-track** ngay sau Phase 3 (trước Phase 5), rồi **TP-005 MVP Preview & Visual** (khoảng trống MVP), rồi Phase 5 Render. Chi tiết thứ tự: mục **MVP Gap & Roadmap** bên dưới.

---

## MVP Gap & Roadmap (Khoảng trống MVP — đã chốt 2026-02-12)

**Bối cảnh:** Để đạt MVP thực sự dùng được, cần bù hai khoảng trống:

1. **Track segment visual — tối thiểu gắn ảnh (hoặc video) vào segment**
   - Hiện tại: `Segment.BackgroundAssetId` và model `Asset` (FilePath) đã có; **thiếu** luồng người dùng: chọn ảnh/file → gán vào segment visual.
   - MVP cần: User có thể gán **ít nhất ảnh** (path hoặc Asset) cho từng segment visual; preview và render dùng đúng ảnh đó. Video clip có thể để bước sau.

2. **Preview — tối thiểu chọn tỉ lệ khung hình + hiển thị theo timeline**
   - **Chọn tỉ lệ:** Preview (canvas) có khung theo aspect ratio (9:16, 16:9, 1:1, 4:5); đồng bộ với render settings.
   - **Preview theo timeline:** Tại mỗi thời điểm playhead, preview hiển thị đúng nội dung: segment **visual** (ảnh tương ứng), segment **text** (subtitle/script), **audio** (đã có play). Tức composite theo track + z-order, sync với timeline.

**Task Pack mới:** **TP-005 MVP VISUAL & PREVIEW** — làm **sau TP-004 core**, **trước Phase 5 (Render Pipeline)**. Phase 5 render từ Canvas/frame composite sẽ tái dùng logic composite từ TP-005.

**Thứ tự ưu tiên tổng thể:**

| Thứ tự | TP / Phase | Trạng thái | Ghi chú |
|--------|------------|------------|--------|
| 1 | Phase 1 (TP-001) | ✅ Done | |
| 2 | Phase 2 (TP-002) | ✅ Done | |
| 3 | Phase 3 (TP-003) | ✅ Done | |
| 4 | Multi-track (TP-004) | ✅ Core done (ST-1..ST-5); ST-6/ST-7 hoãn | |
| 5 | **TP-005 MVP Visual & Preview** | ⏳ **NEXT** | Gắn ảnh vào segment visual; Preview aspect ratio + composite sync timeline. |
| 6 | Phase 5 (Render Pipeline) | ⏳ TODO | #10 output path, #11 render từ Canvas; dùng composite từ TP-005. |
| 7 | Phase 4 (AI & Automation) | ⏳ TODO | Có thể trước/sau Phase 5. |
| 8 | Phase 6 (Polish & QA) | ⏳ TODO | ST-6/ST-7, #12 UI Editor. |

---

## Current Phase: BUILD (TP-005 MVP Visual & Preview)

**Active Task Pack:** TP-005-MVP-VISUAL-AND-PREVIEW  
**Status:** Chưa bắt đầu. Nhiệm vụ tiếp theo: **TP-005** — Gắn ảnh vào segment visual (MVP) + Preview tỉ lệ + Preview composite theo timeline.  
**Chi tiết:** Xem `docs/active.md` (Task Pack TP-005).

**Last Updated:** 2026-02-12 (MVP Gap & TP-005 added)

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

- **TP-004 (Multi-track):** ✅ Core done (ST-1..ST-5). ST-6/ST-7 hoãn (polish).
- **TP-005 (MVP Visual & Preview):** **Tiếp theo** — Gắn ảnh vào segment visual; Preview chọn tỉ lệ + composite theo timeline. Chi tiết ST trong `docs/active.md`.
- **Phase 5 (Render):** Sau TP-005; dùng logic composite từ preview. Phase 4 (AI) và Phase 6 (Polish) theo thứ tự ưu tiên (xem bảng trên).

---

## Phase Commitments (đã ghi trong docs/issues.md)

Các nhiệm vụ sau đã được ghi chi tiết trong **`docs/issues.md`**; khi thực hiện từng phase cần đọc và đưa vào TP/ST tương ứng.

| Phase | Issue(s) | Nội dung tóm tắt |
|-------|----------|-------------------|
| **Phase 3** | #12 (optional), **#13** | #12: UI Editor gọn đẹp CapCut → ưu tiên Phase 6; có thể ST nhỏ. #13: **Audio track tích hợp vào timeline** (waveform/track) — nên có trong TP-003. |
| **Phase 5** | **#10**, **#11** | #10: **Output path**: chọn thư mục xuất, nút "Mở file/thư mục", (tùy chọn) danh sách renders. #11: **Render từ Canvas** (bỏ ảnh tĩnh hardcode); output = frame từ Canvas/segment. |
| **Phase 6** | **#12** | #12: **UI Editor tab** — tối ưu gọn đẹp giống CapCut (layout, spacing, panel collapse). |

**Quy trình:** Session Start đọc `state.md` + `active.md`; khi tạo TP mới hoặc bắt đầu phase → mở `docs/issues.md` và rà số issue trên để đưa vào subtask / acceptance criteria. **Phase Multi-track timeline:** đọc `docs/MULTI-TRACK-TIMELINE-DESIGN.md` trước khi viết ST; tham chiếu trong TP (trong `active.md`).

---

Last updated: 2026-02-12 (MVP Gap & Roadmap; TP-005 next)
