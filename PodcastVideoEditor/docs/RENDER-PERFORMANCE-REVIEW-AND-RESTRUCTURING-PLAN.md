# Render Pipeline Performance Review & Restructuring Plan

## Ngày: 2026-03-31

---

## I. TỔNG QUAN RENDER FLOW HIỆN TẠI

```
StartRenderAsync()
  ├── 1. FFmpegService.InitializeAsync()           (detect ffmpeg, probe GPU encoders)
  ├── 2. CreateRenderSnapshot()                     (deep-clone project + canvas elements)
  ├── 3. RenderSegmentBuilder                       (build tất cả segment types)
  │     ├── BuildTimelineVisualSegments()           (image/video → RenderVisualSegment)
  │     ├── BuildRasterizedTextSegments()           (text → SkiaSharp → PNG files → overlay)
  │     │     └── Parallel.For → TextRasterizer     (CPU parallel rasterize)
  │     ├── BuildVisualizerSegmentsAsync()          (spectrum → SkiaSharp frames → pipe FFmpeg)
  │     │     └── Task.WhenAll → OfflineVisualizerBaker.BakeAsync() per element
  │     │           └── GenerateAndPipeFramesAsync() (frame-by-frame render → rawvideo pipe)
  │     └── BuildTimelineAudioSegments()            (audio clips → RenderAudioSegment)
  ├── 4. FFmpegCommandComposer.Build()              (build filter_complex graph)
  └── 5. FFmpegService.RenderVideoAsync()           (execute ffmpeg, parse progress)
        └── ExecuteFFmpegAsync()                    (Process.Start → parse stderr)
```

---

## II. PHÂN TÍCH HIỆU SUẤT TỪNG THÀNH PHẦN

### A. Visualizer Spectrum Baking (⚠️ BOTTLENECK NGHIÊM TRỌNG NHẤT)

**Hiện trạng:**
```csharp
// OfflineVisualizerBaker.GenerateAndPipeFramesAsync()
for (int frameIdx = 0; frameIdx < totalFrames; frameIdx++)
{
    // 1. Read audio samples → mono mix          (CPU)
    // 2. ComputeFFTMagnitudes()                  (CPU - O(n log n))
    // 3. ProcessSpectrum() (gain/smooth/peak)    (CPU)
    // 4. Render frame via SkiaSharp              (CPU - software rasterize)
    // 5. Marshal.Copy bitmap → byte[]            (CPU memory copy)
    // 6. pipeStream.WriteAsync()                 (I/O - pipe to FFmpeg)
    // FFmpeg encode qtrle                        (CPU - FFmpeg process)
}
```

**Vấn đề:**
1. **100% CPU-bound rendering per frame** — SkiaSharp chạy hoàn toàn trên CPU, không dùng GPU
2. **Sequential frame loop** — mỗi frame phải chờ frame trước write xong mới pipe tiếp
3. **SKBitmap allocation** — dùng chung 1 bitmap nhưng `Marshal.Copy` mỗi frame vẫn tốn ~30MB/s (1920×1080×4 bytes × 30fps)
4. **qtrle codec** — lossless nhưng file size lớn, tốn I/O bandwidth khi FFmpeg đọc lại
5. **Nhiều visualizer chạy parallel** nhưng mỗi cái đều spawn riêng 1 FFmpeg process — tốn RAM nếu có 3-4 visualizer
6. **Không có progress callback** — user không biết visualizer bake đã tới đâu

**Ước tính thời gian:** Video 5 phút @ 30fps = 9000 frames. Mỗi frame ~3-5ms render = **27-45 giây chỉ cho 1 visualizer**.

### B. Text Rasterization (✅ Tốt, cải tiến nhỏ)

**Hiện trạng:**
- `Parallel.For()` rasterize tất cả text segments đồng thời — TỐT
- Mỗi segment = 1 PNG file (static image, không animated)
- SkiaSharp CPU rendering nhưng vì chỉ render 1 lần/segment nên OK

**Vấn đề:**
1. **Per-character DrawText cho letter spacing** — `foreach (var ch in line)` gọi `canvas.DrawText(s, cx, y, paint)` per character. Với 500 ký tự × 3 pass (shadow + outline + fill) = 1500 draw calls per text element
2. **PNG encode per text segment** — `SKImage.Encode(PNG, 100)` quality=100 tốn CPU không cần thiết vì FFmpeg sẽ decode lại ngay
3. **Không cache typeface** — `SKTypeface.FromFamilyName()` gọi lại mỗi text segment dù font giống nhau

### C. Image/Video Overlay (✅ Hợp lý, FFmpeg xử lý tốt)

**Hiện trạng:**
- Image: `-loop 1 -t duration -i "path"` → scale → overlay
- Video: `-hwaccel auto -i "path"` → trim → scale → overlay
- Motion effect: `zoompan` filter trực tiếp trong FFmpeg filter graph

**Vấn đề:**
1. **Mỗi image input = 1 FFmpeg input stream** — 20 image segments = 20 inputs. FFmpeg phải decode + scale tất cả dù chỉ 1 vài visible tại mỗi thời điểm
2. **`overlay` filter chain dài** — N segments = N overlay operations nối tiếp: `[base][s0]overlay→[v0][s1]overlay→[v1]...`. FFmpeg phải giữ tất cả intermediate frames trong RAM
3. **Không có `-thread_queue_size`** — có thể gây buffer underrun khi nhiều inputs
4. **Không dùng `-filter_threads`** — FFmpeg mặc định 1 thread per filter, có thể tăng

### D. Audio Mixing (✅ Tốt)

- `amix` filter với normalize=0 — tốt
- `adelay` cho positioning — tốt
- `afade` cho fade in/out — tốt
- **Vấn đề nhỏ:** `aloop=loop=-1:size=2147483647` cho looping audio — `size=INT_MAX` có thể gây issue với rất dài audio

### E. FFmpeg Execution & Progress (⚠️ Cải tiến được)

**Vấn đề:**
1. **Single FFmpeg process** — toàn bộ filter graph chạy trong 1 process. Nếu có 30 visual segments + 10 text + 2 visualizer = filter graph rất phức tạp, FFmpeg phải parse + validate cả filter trước khi bắt đầu
2. **Progress chỉ parse `time=`** — không report FPS, bitrate, ETA
3. **`-threads 0`** (auto) — nhưng một số filter như `overlay` mặc định single-thread
4. **Không có 2-pass encoding** — single-pass CRF OK cho quality nhưng commercial editors dùng 2-pass cho bitrate-critical output

---

## III. SO SÁNH VỚI CÁC ỨNG DỤNG THƯƠNG MẠI

| Tiêu chí | PodcastVideoEditor (hiện tại) | DaVinci Resolve | Adobe Premiere | CapCut Desktop |
|---|---|---|---|---|
| **Render Engine** | FFmpeg CLI (child process) | Fusion Engine (native GPU) | Mercury Playback Engine (GPU) | FFmpeg + custom C++ engine |
| **GPU Compute** | ❌ Không (chỉ hwaccel decode/encode) | ✅ CUDA/OpenCL/Metal | ✅ CUDA/OpenCL | ✅ Partial GPU |
| **Text Rendering** | Pre-rasterize PNG → overlay | Real-time GPU text | GPU text with caching | Pre-rasterize + GPU composite |
| **Spectrum/VFX** | CPU SkiaSharp → pipe rawvideo | GPU shader real-time | GPU After Effects engine | Pre-bake + GPU composite |
| **Filter Graph** | Monolithic filter_complex | Node-based pipeline | Internal graph scheduler | Layered compositor |
| **Multi-pass** | ❌ Single pass | ✅ Multi-pass option | ✅ VBR 2-pass | ❌ Single pass |
| **Preview→Render** | Separate paths (WPF vs FFmpeg) | Same engine | Same engine | Same engine |
| **Render Cancel** | Kill process | Graceful stop | Graceful stop | Graceful stop |
| **Memory Usage** | FFmpeg manages (moderate) | High (6-16GB) | High (8-16GB) | Moderate |
| **Render Speed (5min 1080p)** | ~60-90s (CPU) / ~30-45s (GPU enc) | ~15-30s (GPU) | ~20-40s (GPU) | ~30-50s |

### Điểm yếu chính so với thương mại:
1. **Không có GPU compute cho VFX** — visualizer/text/motion đều CPU
2. **Preview ≠ Render** — WPF preview dùng code path khác FFmpeg, gây inconsistency
3. **Không có render queue** — chỉ render 1 project tại 1 thời điểm
4. **Không có partial render / smart render** — thay đổi 1 text → render lại toàn bộ
5. **Không có render cache / proxy** — mỗi lần render from scratch

---

## IV. PHÂN LOẠI VẤN ĐỀ THEO MỨC ĐỘ ẢNH HƯỞNG

### 🔴 Critical (ảnh hưởng lớn đến render time)
1. **Visualizer CPU-bound frame loop** — chiếm 30-50% tổng render time
2. **Monolithic filter graph** — FFmpeg phải xử lý graph khổng lồ single-threaded
3. **Không có render caching** — render lại 100% mỗi lần

### 🟡 Important (cải tiến đáng kể)
4. **Text per-character rendering** — 3x draw calls không cần thiết
5. **PNG quality 100** — tốn encode time cho temp files
6. **Typeface không cache** — SkiaSharp resolve font mỗi lần
7. **Thiếu filter_threads / thread_queue_size** — FFmpeg underutilize CPU
8. **qtrle codec cho visualizer** — file lớn, tốn I/O

### 🟢 Minor (polish)
9. **Progress thiếu ETA/FPS** — UX improvement
10. **Temp file cleanup timing** — cleanup có thể fail khi FFmpeg chưa release lock
11. **Hardware encoder không validate capability** — giả sử encoder support nhưng không check resolution/profile limits

---

## V. KẾ HOẠCH TÁI CẤU TRÚC (3 PHASE)

### PHASE 1: Quick Wins — Tối ưu trong kiến trúc hiện tại (1-2 tuần)

#### 1.1 Visualizer Baking Optimization
```
Effort: Medium | Impact: HIGH | Giảm 40-60% thời gian visualizer bake
```

**Thay đổi:**
- **Double-buffered frame pipeline**: Render frame N+1 trên CPU trong khi FFmpeg encode frame N
- **Thay qtrle → `libvpx-vp9` với `-speed 6 -lossless 1`** hoặc **`png` codec** — nhỏ hơn qtrle 3-5x
- **Batch write**: Accumulate 10 frames → write chunk thay vì 1 frame 1 lần
- **Add progress callback** cho visualizer bake → update UI "Baking spectrum: 45%"
- **Pool SKBitmap**: Tạo 2 bitmap luân phiên thay vì 1

```csharp
// BEFORE (sequential):
for (frameIdx = 0; frameIdx < total; frameIdx++)
{
    RenderFrame(bitmap);           // CPU-bound
    CopyBitmapBytes(bitmap, buf);  // memory copy
    await pipe.WriteAsync(buf);    // I/O wait
}

// AFTER (double-buffered):
var buffers = new byte[2][];
buffers[0] = new byte[frameSize];
buffers[1] = new byte[frameSize];
Task? prevWrite = null;
for (frameIdx = 0; frameIdx < total; frameIdx++)
{
    int cur = frameIdx % 2;
    RenderFrame(bitmap);
    CopyBitmapBytes(bitmap, buffers[cur]);
    if (prevWrite != null) await prevWrite;   // overlap render + I/O
    prevWrite = pipe.WriteAsync(buffers[cur], 0, frameSize, ct);
}
if (prevWrite != null) await prevWrite;
```

#### 1.2 Text Rasterization Speedup
```
Effort: Low | Impact: MEDIUM | Giảm 50% text render time
```

- **Cache SKTypeface** per font family (static `ConcurrentDictionary<string, SKTypeface>`)
- **Batch letter-spacing**: Dùng `SKTextBlob` builder thay vì per-character DrawText
- **PNG quality 80** thay vì 100 cho temp files (FFmpeg decode quality = nhau)
- **Reuse SKPaint** objects thay vì create/dispose mỗi segment

#### 1.3 FFmpeg Filter Graph Optimization
```
Effort: Low | Impact: MEDIUM | Giảm 10-20% FFmpeg render time
```

- Thêm `-filter_threads 4` vào FFmpeg args
- Thêm `-thread_queue_size 512` cho mỗi input
- Thêm `setpts=PTS-STARTPTS` sớm hơn trong chain để FFmpeg optimize
- Dùng `overlay=shortest=0:repeatlast=0` thay vì `eof_action=pass` (giảm memory)
- Nhóm các segments cùng time range vào sub-graphs để FFmpeg optimize scheduling

#### 1.4 Parallel Visualizer + Text Preparation
```
Effort: Low | Impact: MEDIUM
```

- Chạy text rasterization SONG SONG với visualizer baking (hiện tại sequential)
```csharp
// BEFORE:
var textSegs = BuildRasterizedTextSegments(...);
var vizSegs = await BuildVisualizerSegmentsAsync(...);

// AFTER:
var textTask = Task.Run(() => BuildRasterizedTextSegments(...));
var vizTask = BuildVisualizerSegmentsAsync(...);
await Task.WhenAll(textTask, vizTask);
var textSegs = textTask.Result;
var vizSegs = vizTask.Result;
```

---

### PHASE 2: Architectural Improvements (3-4 tuần)

#### 2.1 Render Cache System
```
Effort: High | Impact: VERY HIGH | Giảm 80-90% thời gian re-render
```

**Concept:** Cache intermediate render output per segment. Khi user chỉ thay đổi 1 text overlay → chỉ re-render segment đó, giữ nguyên tất cả segments khác.

```
render_cache/
  project_{id}/
    visual_seg_{hash}.ts      // MPEG-TS fragment per visual segment
    text_seg_{hash}.png       // Rasterized text (already exists)
    viz_seg_{hash}.mov        // Baked visualizer (already exists)
    manifest.json             // Hash → file mapping + timestamps
```

**Hash** = SHA256(sourcePath + startTime + endTime + overlayX + overlayY + scaleW + scaleH + motionPreset + ...)

**Flow:**
1. Trước render: kiểm tra cache manifest
2. Chỉ re-build segments có hash thay đổi
3. Final compose: concat cached fragments + new segments

#### 2.2 Segmented Render Pipeline (Split-Compose)
```
Effort: High | Impact: HIGH | Cho phép parallel FFmpeg per segment
```

Thay vì 1 monolithic filter graph, split thành:

```
Phase A — Pre-render (parallel):
  ├── FFmpeg Process 1: visual_segment_0.ts (scale + motion + encode)
  ├── FFmpeg Process 2: visual_segment_1.ts
  ├── FFmpeg Process 3: visual_segment_2.ts
  └── ... (bounded by CPU cores)

Phase B — Composite (single FFmpeg):
  └── FFmpeg: overlay all pre-rendered segments + audio → final.mp4
```

**Lợi ích:**
- Tận dụng multi-core tốt hơn (hiện tại 1 FFmpeg process)
- Mỗi segment nhỏ → filter graph đơn giản → FFmpeg optimize tốt hơn
- Fail 1 segment → retry riêng, không cần restart toàn bộ
- Cache-friendly (Phase 2.1)

#### 2.3 Hardware-Accelerated Visualizer via SkiaSharp GPU Backend
```
Effort: Medium | Impact: HIGH | Giảm 70-80% visualizer render time
```

SkiaSharp hỗ trợ OpenGL backend (GRContext). Chuyển từ CPU canvas sang GPU:

```csharp
// BEFORE (CPU):
using var bitmap = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
using var canvas = new SKCanvas(bitmap);

// AFTER (GPU via GRGlInterface):
using var glInterface = GRGlInterface.CreateOpenGl();
using var grContext = GRContext.CreateGl(glInterface);
using var surface = SKSurface.Create(grContext, false, new SKImageInfo(outW, outH));
var canvas = surface.Canvas;
// ... render ...
// Read pixels back for piping to FFmpeg
surface.ReadPixels(imageInfo, destPtr, rowBytes, 0, 0);
```

**Lưu ý:** Cần headless OpenGL context (EGL hoặc WGL offscreen). Trên Windows có thể dùng ANGLE.

#### 2.4 Smart Render (Selective Re-render)
```
Effort: Medium | Impact: HIGH
```

Track dirty state per segment:
```csharp
public class RenderDirtyTracker
{
    // Hash mỗi segment's render-relevant properties
    // Khi StartRender: so sánh current hash vs cached hash
    // Chỉ re-render segments có hash khác
    Dictionary<string, string> _segmentHashes;
    
    public bool IsSegmentDirty(string segmentId, string newHash)
        => !_segmentHashes.TryGetValue(segmentId, out var old) || old != newHash;
}
```

---

### PHASE 3: Commercial-Grade Architecture (2-3 tháng)

#### 3.1 Unified Render Engine (Preview = Export)
```
Effort: VERY HIGH | Impact: CRITICAL cho long-term
```

**Vấn đề gốc:** Preview dùng WPF + MotionAnimator + VisualizerService. Export dùng FFmpeg + MotionFilterBuilder + OfflineVisualizerBaker. Hai code path hoàn toàn khác nhau → inconsistency.

**Giải pháp:** Build một render engine duy nhất dựa trên SkiaSharp GPU:

```
┌─────────────────────────────────────────────────┐
│              Unified Compositor                  │
│  (SkiaSharp GPU backend via OpenGL/Vulkan)       │
│                                                  │
│  Input:  Frame request (timestamp)               │
│  Output: RGBA pixel buffer                       │
│                                                  │
│  Per frame:                                      │
│   1. Resolve active segments at timestamp         │
│   2. Render each layer (image/video/text/viz)    │
│   3. Composite with alpha blending + transforms  │
│   4. Return pixel buffer                         │
│                                                  │
│  Used by:                                        │
│   • Preview: render → WPF WriteableBitmap        │
│   • Export:  render → pipe to FFmpeg encoder      │
└─────────────────────────────────────────────────┘
```

**Lợi ích:**
- WYSIWYG hoàn hảo — preview = export pixel-perfect
- Code DRY — 1 renderer thay vì 2 systems
- GPU acceleration cho cả preview và export
- Dễ thêm effects mới (chỉ implement 1 lần)

#### 3.2 Render Queue & Background Render
```
Effort: Medium | Impact: HIGH
```

- Render queue cho phép queue nhiều exports
- Background render không block UI
- Pause/resume render
- Priority system (user có thể render preview quality trước, high quality sau)

```csharp
public class RenderQueue
{
    private readonly Channel<RenderJob> _queue = Channel.CreateUnbounded<RenderJob>();
    
    public async Task EnqueueAsync(RenderJob job);
    public async Task ProcessQueueAsync(CancellationToken ct);
    // Worker chạy trong background thread, report progress qua IProgress<T>
}
```

#### 3.3 Proxy Media System
```
Effort: High | Impact: HIGH cho 4K workflows
```

- Import 4K video → auto-generate 720p proxy
- Timeline/preview dùng proxy → mượt mà
- Export dùng original → full quality
- Giống Premiere Pro "proxy workflow"

```csharp
public class ProxyMediaService
{
    public async Task<string> CreateProxyAsync(string originalPath, ProxyQuality quality);
    public string ResolveMediaPath(string assetId, bool useProxy);
}
```

#### 3.4 GPU Shader Effects Pipeline
```
Effort: VERY HIGH | Impact: TRANSFORMATIVE
```

Dùng compute shaders (OpenGL/Vulkan/DirectX) cho real-time effects:
- Color grading (LUT application)
- Blur, glow, chromatic aberration
- Custom transitions (dissolve, wipe, slide)
- Spectrum visualization trực tiếp trên GPU

```glsl
// Ví dụ: spectrum bars fragment shader
uniform float spectrum[64];
uniform vec3 color1, color2;

void main() {
    int band = int(gl_FragCoord.x / barWidth);
    float magnitude = spectrum[band];
    float y = gl_FragCoord.y / height;
    gl_FragColor = y < magnitude ? mix(color1, color2, y) : vec4(0);
}
```

---

## VI. TIMELINE & ĐỘ ƯU TIÊN

```
       Tuần 1-2          Tuần 3-4          Tuần 5-8          Tháng 3-5
    ┌──────────┐    ┌──────────────┐   ┌──────────────┐   ┌───────────────┐
    │ PHASE 1  │    │  PHASE 2.1   │   │  PHASE 2.2   │   │  PHASE 3.1    │
    │ Quick    │───▶│  Render      │──▶│  Segmented   │──▶│  Unified      │
    │ Wins     │    │  Cache       │   │  Pipeline    │   │  Compositor   │
    │          │    │              │   │              │   │               │
    │ • Viz    │    │  PHASE 2.3   │   │  PHASE 2.4   │   │  PHASE 3.2-4  │
    │   buffer │    │  GPU SkiaS.  │   │  Smart       │   │  Queue/Proxy  │
    │ • Text   │    │              │   │  Render      │   │  /GPU Shader  │
    │   cache  │    │              │   │              │   │               │
    │ • FFmpeg │    │              │   │              │   │               │
    │   threads│    │              │   │              │   │               │
    └──────────┘    └──────────────┘   └──────────────┘   └───────────────┘
    
    Impact:          Impact:            Impact:            Impact:
    -20-30%          -50-70%            -30-40%            GAME-CHANGER
    render time      re-render time     first render       Commercial parity
```

---

## VII. HIỆU QUẢ DỰ KIẾN

### Trước khi tối ưu (5 phút video, 1080p, 10 images + 5 text + 1 visualizer):
| Phase | Thời gian | Bottleneck |
|-------|-----------|------------|
| Visualizer bake | ~35s | CPU SkiaSharp per-frame |
| Text rasterize | ~3s | Per-character draw calls |
| FFmpeg encode | ~45s | Single-thread filter graph |
| **Tổng** | **~85s** | |

### Sau Phase 1 (Quick Wins):
| Phase | Thời gian | Cải tiến |
|-------|-----------|---------|
| Visualizer bake | ~20s | Double-buffer + codec |
| Text rasterize | ~1.5s | Cache + batch |
| FFmpeg encode | ~35s | Multi-thread filters |
| **Tổng** | **~58s** | **-32%** |

### Sau Phase 2 (Architectural):
| Phase | Thời gian (first render) | Thời gian (re-render, 1 text changed) |
|-------|--------------------------|---------------------------------------|
| Visualizer | ~8s (GPU SkiaSharp) | 0s (cached) |
| Text | ~1s | ~0.5s (1 segment only) |
| FFmpeg | ~25s (parallel segments) | ~5s (compose cached + 1 new) |
| **Tổng** | **~35s** | **~6s** |

### Sau Phase 3 (Commercial-grade):
| Tiêu chí | Thời gian |
|-----------|-----------|
| First render | ~20-25s (GPU compositor + hardware encode) |
| Re-render | ~3-5s (smart render) |
| Preview latency | <16ms (real-time 60fps GPU) |

---

## VIII. KẾT LUẬN

### Điểm mạnh hiện tại:
- ✅ Architecture sạch, separation of concerns tốt (Builder pattern, Composer pattern)
- ✅ Hardware encoder auto-detection (NVENC/QSV/AMF fallback chain)
- ✅ Deep-clone snapshot trước render (no race conditions)
- ✅ Parallel text rasterization và visualizer baking
- ✅ Temp file management với unique GUID dirs

### Điểm cần cải thiện ngay (Phase 1):
- ⚠️ Visualizer frame loop cần double-buffering
- ⚠️ FFmpeg cần `-filter_threads` / `-thread_queue_size`
- ⚠️ Text typeface cần cache
- ⚠️ Parallel text + visualizer (hiện sequential)

### Để cạnh tranh được với DaVinci/Premiere/CapCut:
- 🎯 **Unified Compositor** là mục tiêu chiến lược (Phase 3.1) — một engine cho cả preview lẫn export
- 🎯 **Render Cache + Smart Render** (Phase 2) — đây là feature mà user cảm nhận rõ nhất
- 🎯 **GPU compute for visualizer/effects** — chìa khóa để real-time preview 60fps

Kiến trúc hiện tại (FFmpeg CLI as render backend) là **pragmatic và đúng đắn cho giai đoạn này**, nhưng để scale lên commercial-grade cần migrate sang unified GPU compositor trong tương lai.
