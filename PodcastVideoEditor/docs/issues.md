# Issues & Blockers Tracker

**Project:** Podcast Video Editor  
**Updated:** 2026-02-08

**Cross-references (quy trình):** Các issue #10–#13 gắn với Phase 3/5/6. Khi tạo TP hoặc bắt đầu phase, đọc **`docs/state.md`** mục **Phase Commitments** và đưa issue tương ứng vào ST. Tham chiếu ngược: `state.md`, `active.md` (Resume Instructions), `decisions.md` (Forward References).

---

## Active Issues

### Issue #1: FFmpeg Installation Validation
**Status:** 📋 TODO (Phase 1)  
**Priority:** 🔴 BLOCKER

**Description:**
User states they have FFmpeg installed, but we need to validate:
1. FFmpeg binary is in system PATH
2. Correct version installed (≥4.4 recommended)
3. GPU acceleration available (NVIDIA/Intel)

**Acceptance Criteria:**
- [ ] App detects FFmpeg on startup
- [ ] Warning if version too old
- [ ] User can override path in Settings
- [ ] Graceful error if FFmpeg not found

**Assigned to:** Phase 1 - ST-1 (Setup)  
**Effort:** 2 hours  
**Notes:** Critical for basic render pipeline

---

### Issue #2: Audio Format Support
**Status:** 📋 TODO (Phase 1)  
**Priority:** 🟡 MEDIUM

**Description:**
NAudio supports MP3, WAV, FLAC but may have issues with:
- M4A (AAC codec) - requires CoreAudio on Windows
- DSD files
- Video files with audio (MKV, MP4 as audio source)

**Questions:**
- Should we support M4A? (Yes, common podcast format)
- Limit to audio formats only or allow video file input?

**Assigned to:** Phase 1 - ST-3  
**Decision Needed:** 🟡 DECIDE  
**Notes:** Yescale may support more formats; defer audio codec expansion to Phase 2 if NAudio limitation hit

---

### Issue #3: Memory Management with SkiaSharp
**Status:** 📋 TODO (Phase 2)  
**Priority:** 🔴 BLOCKER (Phase 2)

**Description:**
SkiaSharp Bitmap objects must be disposed immediately after use. If not, rendering thousands of frames for FFmpeg will cause memory leak.

**Test Plan:**
- [ ] Render 10-minute video at 60fps (36000 frames) in offline mode
- [ ] Monitor RAM during render (should not exceed 500MB for cache)
- [ ] Use dotMemory to profile for leaks

**Assigned to:** Phase 2 - VisualizerService  
**Effort:** 4 hours  
**Notes:** This is a known risk; must address before Phase 5 (Render Pipeline)

---

### Issue #4: FFmpeg Complex Filter Command Length
**Status:** 📋 TODO (Phase 5)  
**Priority:** 🟡 MEDIUM

**Description:**
When combining:
- Multiple background images (concat filter)
- Visualizer overlay (image piped from stdin)
- Text overlays
- Audio mixing (amix)
- BGM with fade (afade)

The `-filter_complex` command can exceed Windows command line limit (~32KB).

**Solution:**
- Use FFmpeg filter file (`.txt`) instead of inline command
- Or split into multiple render passes

**Assigned to:** Phase 5 - FFmpegService  
**Effort:** 3 hours  
**Notes:** Affects complex projects with many tracks + visualizer

---

### Issue #5: Timeline Sync Precision
**Status:** 📋 TODO (Phase 3)  
**Priority:** 🟡 MEDIUM

**Description:**
When playing audio and displaying segments on timeline:
- Audio position should sync to displayed segment within ±50ms
- FFT data might lag slightly (NAudio callback latency)

**Edge Cases:**
- Seek while playing (discontinuous jump)
- Very fast video (60fps, 16ms per frame)
- Long silence in audio (FFT shows nothing)

**Test Scenarios:**
- [ ] Play 10-minute podcast, sample every 5 seconds
- [ ] Seek randomly, verify segment updates
- [ ] Measure latency (timestamp vs UI update)

**Assigned to:** Phase 3 - TimelineViewModel  
**Effort:** 4 hours  
**Notes:** Important for user experience; not blocker if slightly off (e.g., ±100ms)

---

### Issue #6: Database Migration & Versioning
**Status:** 📋 TODO (Phase 1)  
**Priority:** 🟡 MEDIUM

**Description:**
When updating schema (Phase 2: add new fields, Phase 3: schema changes):
- Existing project files should still open
- Migration path must be transparent to user
- Don't require manual backup/restore

**Decision:**
- Use EF Core migrations (auto-apply on startup)
- Version number in schema (Version table)
- Backup DB before major migration

**Assigned to:** Phase 1 - ST-2 (Database)  
**Effort:** 2 hours  
**Notes:** Setup now to avoid rework later

---

### Issue #7: Yescale API Key Management
**Status:** 🟡 TBD  
**Priority:** 🟡 MEDIUM

**Description:**
Questions about Yescale API:
- Does it require authentication header or query param?
- Rate limits? (e.g., X calls per minute)
- Latency? (critical for UX: user clicks "Auto Segment", waits for result)
- Cost model? (per call, monthly subscription, etc.)
- Fallback if API fails?

**Decision Needed:** 🟡 GET API SPEC  
**Notes:** Phase 4 task, but specs needed now for planning

---

### Issue #8: GPU Acceleration (NVIDIA/Intel)
**Status:** 📋 TODO (Phase 5)  
**Priority:** 🟢 LOW (v1.0)

**Description:**
FFmpeg can use GPU for encoding (NVIDIA NVENC, Intel QSV). This can speed up render 5-10x.

**Decision:** Phase 1-5 use CPU render (simpler), Phase 6 (Polish) add GPU option if time.

**Notes:**
- User can install NVIDIA drivers separately
- Detect GPU on startup via FFmpeg `-codecs` query
- Add toggle in Settings: "Use GPU acceleration if available"

---

### Issue #9: Audio Codec License Compliance
**Status:** 📋 TODO  
**Priority:** 🟡 MEDIUM

**Description:**
MP3 codec had licensing issues historically. Verify:
- NAudio MP3 handling (uses MPEG Layer III Library or similar)
- Any license headers needed in binary distribution

**Action:** Check NAudio license file + MP3 decoder source

**Notes:** Low risk (NAudio is established lib), but verify before release

---

## Resolved Issues

(None yet - project just started)

---

## Known Limitations & Trade-offs

### Limitation #1: Single Render at a Time
- **Current:** Only 1 video can render simultaneously
- **Why:** Keeps code simple, prevents resource contention
- **Workaround:** User can queue projects manually
- **Future:** v2.0 backend service for concurrent renders

### Limitation #2: Windows-Only
- **Current:** WPF only runs on Windows
- **Why:** Desktop focused on Windows per user requirement
- **Workaround:** None (design choice)
- **Future:** v2.0 may add web interface for cross-platform

### Limitation #3: No Real-Time Collab Editing
- **Current:** 1 user per project, local file only
- **Why:** v1.0 is personal tool
- **Workaround:** Export template, share project file
- **Future:** v2.0 cloud storage + shared editing

### Limitation #4: Audio Preview Latency
- **Current:** ~100-200ms between UI seek and audio update
- **Why:** NAudio callback overhead
- **Workaround:** Accept slight delay (not noticeable in practice)
- **Future:** Use WASAPI loopback for lower latency

---

## Assumptions

1. **FFmpeg is installed** on user's machine (user confirmed)
2. **API keys** are cheap/free or user has budget (Yescale, Unsplash)
3. **Render time** acceptable for 1080p video: <5 min for 10-min podcast
4. **RAM** available on user machine: ≥4GB (typical modern PC)
5. **SSD** storage: Fast enough for temp files during render

---

## Dependencies to Verify

- [ ] FFmpeg version check (user: "I have FFmpeg" → validate version)
- [ ] NAudio MP3 codec availability
- [ ] SkiaSharp build for .NET 8 Windows
- [ ] Yescale API documentation & quotas
- [ ] Unsplash/Pexels/Pixabay API free tier limits

---

## Review Schedule

- **Weekly:** Check blockers during active dev
- **Phase End:** Reassess priority/effort
- **Monthly:** Cleanup resolved issues

---

Last updated: 2026-02-06

Next review: Phase 1 completion (2026-02-27)
