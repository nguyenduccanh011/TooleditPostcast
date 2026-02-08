# Phase 2 Quick Start Guide

## 📋 What's Completed (Phase 1 ✅)

The project has finished **Phase 1** with all core infrastructure:

- ✅ **Project Management**: Create, save, load projects
- ✅ **Audio Service**: Load, play, pause, seek audio files
- ✅ **Database**: SQLite with EF Core migrations
- ✅ **Render Pipeline**: Basic FFmpeg integration for video output
- ✅ **UI Foundation**: MVVM architecture, 3-tab layout (Home, Editor, Settings)

**Latest Status:** Phase 2 in progress (ST-7 and ST-8 complete)

---

## 🎯 Phase 2 Objectives

Phase 2 focuses on **visual editing and real-time visualization**:

### 1. Canvas Editor (ST-7)
- Drag-drop interface for placing elements
- Support 5 element types: Title, Logo, Image, Visualizer, Text
- Property editing for each element
- Z-order management (layering)

### 2. Visualizer Service (ST-8)
- Real-time spectrum visualization using SkiaSharp
- Subscribe to audio FFT data
- Multiple styles: Bars, Waveform, Circular
- Multiple color palettes: Rainbow, Fire, Ocean, Mono, Purple

### 3. Timeline Editor (ST-9)
- Segment management (start time, duration, background image)
- Playhead sync with audio
- Visual timeline with ruler and grid
- Segment property editing panel

### 4. Canvas + Visualizer Integration (ST-10)
- Render visualizer element on canvas
- Real-time 60fps spectrum display
- Memory-efficient rendering

### 5. Property Editor Panel (ST-11)
- Dynamic property editing by element type
- Two-way binding with canvas selection
- Color pickers, sliders, text inputs

### 6. Unified Editor Layout (ST-12)
- One Editor workspace like CapCut
- Canvas + Toolbar + Properties + Timeline + Audio/Render in one screen
- No tab switching for preview

---

## 📊 Phase 2 Planning

**Total Effort:** ~36 hours  
**Target Duration:** 3-4 weeks  
**Subtasks:** 6 (ST-7 through ST-12)

### Task Breakdown

| Task | Hours | Priority | Status |
|------|-------|----------|--------|
| ST-7: Canvas Infrastructure | 6 | 🔴 First | ✅ DONE |
| ST-8: Visualizer Service | 8 | 🔴 Critical | ✅ DONE |
| ST-9: Timeline Editor | 7 | 🟡 High | 🏁 NEXT |
| ST-10: Canvas Integration | 4 | 🟡 High | ⏳ PENDING |
| ST-11: Property Editor | 5 | 🟡 Medium | ⏳ PENDING |
| ST-12: Unified Editor Layout | 6 | 🟡 Medium | ⏳ PENDING |

---

## 🚀 How to Get Started

### 1. Review Phase 2 Planning
Read the detailed plan: **[Phase 2 Plan](phase2-plan.md)**

Key sections:
- ST-7 to ST-12 detailed requirements
- Acceptance criteria for each task
- Dependencies and integration flow
- Risk assessment and mitigations

### 2. Check Current Code
- **Models:** `src/PodcastVideoEditor.Core/Models/`
- **Services:** `src/PodcastVideoEditor.Core/Services/`
- **Views:** `src/PodcastVideoEditor.Ui/Views/`
- **ViewModels:** `src/PodcastVideoEditor.Ui/ViewModels/`

### 3. Build and Run
```bash
cd src
dotnet build PodcastVideoEditor.slnx
dotnet run --project PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj
```

### 4. Create a Test Project
In the app:
1. Click "New Project"
2. Name it (e.g., "Phase 2 Test")
3. Select an audio file (WAV, MP3, FLAC, M4A)
4. Click Create

### 5. Verify Phase 1 Features
- Load project
- Play audio (Audio Player tab)
- Render a simple video (Editor tab → Render section)
- View render output

---

## 🛠️ Development Workflow

### Phase 2 Starting Point: ST-9

1. **Create Timeline Infrastructure**
   - Create `TimelineView.xaml`
   - Create `TimelineViewModel`
   - Sync playhead with audio

2. **Test Timeline**
   - Add segments
   - Drag/resize segments
   - Verify playhead sync

3. **Continue with ST-10, ST-11, ST-12**
   - Follow the dependency flow in phase2-plan.md
   - Test each subtask before moving to next

---

## 📚 Key Documentation

| Document | Purpose |
|----------|---------|
| [phase2-plan.md](phase2-plan.md) | Detailed Phase 2 task breakdown |
| [active.md](active.md) | Current task pack status |
| [state.md](state.md) | Project metadata, scope, timeline |
| [arch.md](arch.md) | Architecture and design patterns |
| [code_rules.md](code_rules.md) | Coding standards |
| [issues.md](issues.md) | Known issues and blockers |

---

## 🔍 Current Database Status

**Location:** `C:\Users\DUC CANH PC\AppData\Roaming\PodcastVideoEditor\app.db`

**Schema:**
- Projects
- Segments (one-to-many with Projects)
- Elements (one-to-many with Projects)
- Assets (audio/image files)
- BgmTracks (background music)
- Templates (saved layouts)

**Sample Query:** List all projects
```sql
SELECT * FROM Projects;
```

---

## ⚠️ Known Issues & Risks

### Phase 2 Blockers

1. **Issue #3: SkiaSharp Memory Management**
   - Bitmap disposal must be exact
   - Test with 10+ minute audio
   - Mitigation: dotMemory profiling before Phase 3

2. **Timeline Sync Precision**
   - Playhead may drift from audio position
   - Acceptable: ±50ms latency
   - Test with sample files early

3. **Canvas Performance**
   - 20+ elements may cause lag
   - Mitigation: Implement virtualization if needed

---

## 🎮 Testing Commands

### Build
```bash
cd src
dotnet build PodcastVideoEditor.slnx
```

### Run
```bash
dotnet run --project PodcastVideoEditor.Ui/PodcastVideoEditor.Ui.csproj
```

### Clean (if needed)
```bash
dotnet clean PodcastVideoEditor.slnx
```

---

## 📞 Questions?

- Check [phase2-plan.md](phase2-plan.md) for detailed requirements
- Review [arch.md](arch.md) for architectural patterns
- See [code_rules.md](code_rules.md) for coding standards
- Check [issues.md](issues.md) for known problems

---

## 🎯 Phase 2 Success Criteria

✅ All subtasks (ST-7 to ST-12) marked DONE  
✅ 0 build errors  
✅ Manual test plan passed  
✅ Visualizer rendering live spectrum correctly  
✅ Canvas + Timeline + Properties working together  
✅ Performance targets met (<500MB RAM, 60fps visualizer)  
✅ Ready for Phase 3 (Script & Timeline automation)  

---

**Last Updated:** 2026-02-07  
**Next Focus:** ST-9 Timeline Editor  
**Estimated Timeline:** Feb 7 - Mar 7, 2026
