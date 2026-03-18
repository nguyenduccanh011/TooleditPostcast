# 🏗️ AI Script Processing Architecture Documentation

**Postcast Project - AI-Powered Podcast Workflow**

This document provides a comprehensive overview of the architecture, data flow, and system design for the AI script analysis and background image selection features in Postcast.

---

## 📚 Table of Contents

1. [System Overview](#system-overview)
2. [8-Stage Workflow](#8-stage-workflow)
3. [Service Architecture](#service-architecture)
4. [Data Models & Types](#data-models--types)
5. [Module Structure](#module-structure)
6. [Integration Points](#integration-points)
7. [Provider Architecture](#provider-architecture-spi-pattern)
8. [Storage & Persistence](#storage--persistence)
9. [Error Handling Strategy](#error-handling-strategy)
10. [Performance Considerations](#performance-considerations)

---

## System Overview

The Postcast AI system consists of **two main phases**:

```
┌─────────────────────────────────────────────────────────────────────┐
│                         POSTCAST AI SYSTEM                         │
└─────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────┐    ┌──────────────────────────────┐
│    PHASE 1: SCRIPT ANALYSIS  │    │  PHASE 2: IMAGE SELECTION    │
├──────────────────────────────┤    ├──────────────────────────────┤
│                              │    │                              │
│ Input: Podcast Script        │    │ Input: AI Segments           │
│                              │    │                              │
│ Process:                     │    │ Process:                     │
│ • Normalize script (ASR fix) │    │ • Fetch candidates (3x)      │
│ • Extract segments           │    │ • Batch into groups of 10    │
│ • Identify keywords (5 each) │    │ • Send to AI for selection   │
│ • Generate descriptions      │    │ • Validate & retry if needed │
│                              │    │ • Allow manual override      │
│ Output:                      │    │                              │
│ • AISegment[] with:          │    │ Output:                      │
│   - startTime/endTime        │    │ • AISelectionMeta[] with:    │
│   - text                     │    │   - chosen: ID               │
│   - keywords[5]              │    │   - backups[]                │
│   - description              │    │   - reason (Vietnamese)      │
│                              │    │                              │
└──────────────────────────────┘    └──────────────────────────────┘

         ↓ Saved to localStorage   ↓ User selects backgrounds

         Background Page → Timeline Editor → Video Export
```

**Key Characteristics:**
- **Phase 1** is AI-driven (model generates segments from script)
- **Phase 2** is model + user-driven (AI suggests, user confirms or overrides)
- Both phases use **YesScale API** (OpenAI-compatible, gpt-4o, gpt-4-turbo, gpt-3.5-turbo)
- Both phases support **preset management** (save/load custom configurations)
- Both phases have **retry logic** for transient failures

---

## 8-Stage Workflow

### **Stage 1: Script Input & Normalization** (Upload Page)

```
User Input
    ↓
┌─────────────────────────────┐
│ Script Text (Vietnamese)    │
│ Audio File (MP3/WAV)        │
│ Duration (auto-detected)    │
└──────────┬──────────────────┘
           │
           ✓ Validation: Non-empty, audio format OK
           │
           ↓
┌──────────────────────────────────┐
│ Optional: Normalize with AI       │  (normalizeScriptWithAI)
│ Fix ASR errors:                  │
│  • "tỏ mò" → "tò mò"            │
│  • "vũn hóa" → "vốn hóa"        │
│  • "cốt loại" → "cốt lõi"       │
└──────────┬───────────────────────┘
           │
           ↓
┌──────────────────────────────────┐
│ Save to localStorage             │
│  • podcast_script                │
│  • podcast_audio_duration        │
│  • podcast_audio_blob (optional) │
└──────────┬───────────────────────┘
           │
           ↓
      Ready for Analysis
```

**Key Points:**
- ASR (Audio-to-Text) errors corrected before analysis
- Saves script + duration for later steps
- Audio file stored (IndexedDB for large files)

---

### **Stage 2: Script Analysis with AI** (AIAnalysisPanel)

```
User clicks: "Phân tích AI"
    ↓
┌─────────────────────────────────────┐
│ AIAnalysisPanel Component           │
│ ├─ Select Model (gpt-4o default)    │
│ ├─ Verify API Key                   │
│ ├─ Show Progress (0-100%)           │
│ └─ Display Results                  │
└────────────┬────────────────────────┘
             │
             ↓
┌─────────────────────────────────────┐
│ useAIAnalysis Hook                  │
│ ├─ Validate API key                 │
│ ├─ Fetch available models           │
│ ├─ Build analysis request           │
│ ├─ Call YesScaleProvider            │
│ ├─ Handle errors/retries            │
│ └─ Return AIAnalysisResponse        │
└────────────┬────────────────────────┘
             │
             ↓
┌─────────────────────────────────────┐
│ YesScaleProvider.analyzeScript()    │
│ ├─ buildUserPrompt()                │
│ │  └─ Add script + duration context │
│ ├─ fetchWithRetry()                 │
│ │  ├─ POST /chat/completions        │
│ │  ├─ Timeout: 90s                  │
│ │  └─ Retry: exp backoff (1s,2s,4s) │
│ └─ parseAIResponse()                │
│    ├─ Extract JSON from response    │
│    ├─ Validate timestamps           │
│    ├─ Validate keywords             │
│    ├─ Repair malformed segments     │
│    └─ Return AISegment[]            │
└────────────┬────────────────────────┘
             │
             ↓
┌─────────────────────────────────────┐
│ Store Results                       │
│ ├─ podcast_ai_segments              │
│ ├─ podcast_ai_analysis_result       │
│ ├─ podcast_ai_model (selected)     │
│ └─ podcast_ai_analysis_preset_id    │
└────────────┬────────────────────────┘
             │
             ↓
   Segments Ready for Image Selection
```

**Data Produced:**
```typescript
AIAnalysisResponse = {
  segments: AISegment[],  // Array of analyzed segments
  rawResponse: string     // Raw AI response for debugging
}

AISegment = {
  startTime: number,           // e.g., 0.0
  endTime: number,             // e.g., 5.58
  text: string,                // Segment script content
  keywords: string[],          // 5 English keywords
  description: string,         // Brief description
  suggestedBackgrounds?: string // Optional AI suggestion
}
```

---

### **Stage 3: Image Candidate Search** (Background Page Load)

```
Page navigates to /background
    ↓
┌────────────────────────────────────┐
│ BackgroundSelectionPage Component  │
└────────────┬───────────────────────┘
             │
             ├─ Load segments from localStorage
             ├─ Load previously selected images
             ├─ Load visual settings
             │
             ↓
┌──────────────────────────────────────────┐
│ For Each Segment in Parallel             │
│ (Or on-demand when user selects segment) │
└────────────┬─────────────────────────────┘
             │
             ├─ getSearchQueryForSegment()
             │  └─ Concatenate first 3 keywords
             │     Example: "fed stock banking"
             │
             ├─ fetchCandidatesForSegment()
             │  │
             │  ├─ Promise.allSettled() for 3 providers:
             │  │
             │  ├─ PEXELS
             │  │  ├─ GET /search?query=fed+stock+banking
             │  │  ├─ Per page: 20
             │  │  └─ Returns: 20 BackgroundImage objects
             │  │
             │  ├─ PIXABAY
             │  │  ├─ GET /?q=fed+stock+banking
             │  │  ├─ Per page: 20
             │  │  └─ Returns: 20 BackgroundImage objects
             │  │
             │  ├─ UNSPLASH
             │  │  ├─ GET /search/photos?query=fed+stock+banking
             │  │  ├─ Per page: 20
             │  │  └─ Returns: 20 BackgroundImage objects
             │  │
             │  └─ Aggregate Results
             │     └─ ~60 unique candidates with compositeId
             │
             └─ Return: {candidates, imageMap}

Result per segment:
candidates = [
  {id: "pexels:123456", semantic: "Federal Reserve chairman..."},
  {id: "pixabay:789012", semantic: "gold coins stack..."},
  ...
]
```

**Image Data Structure:**
```typescript
BackgroundImage = {
  id: string,              // Provider ID
  source: 'pexels' | 'pixabay' | 'unsplash',
  url: string,             // Display URL
  downloadUrl?: string,    // High-resolution download
  description: string,     // Image title/description
  tags?: string[],         // Search tags
  width: number,           // Image dimensions
  height: number,
  photographer?: string,   // Attribution
  attributionUrl?: string
}
```

---

### **Stage 4: AI Image Selection** (User Clicks "Select with AI")

```
User selects segment + clicks "Select Images with AI"
    ↓
┌─────────────────────────────────┐
│ useAISelectBackgrounds Hook      │
│ ├─ Load preset (system prompt)   │
│ ├─ Validate segments             │
│ ├─ Call selectBackgroundsService │
│ └─ Manage loading/progress state │
└────────────┬────────────────────┘
             │
             ↓
┌─────────────────────────────────────────────┐
│ selectBackgroundsService.runSelectBackgrounds│
│                                             │
│ 1. Batch segments into groups of 10        │
│    Max parallel batches: 3                  │
│                                             │
│ 2. For each batch:                         │
│                                             │
│    a) fetchCandidatesForSegment() in parallel
│       └─ ~60 candidates per segment        │
│                                             │
│    b) callAISelectImages(batch)            │
│       ├─ POST to YesScale /chat/completions
│       ├─ Temperature: 0.3 (deterministic)  │
│       ├─ Input: segments + candidates      │
│       ├─ Output: chosen + backups + reason │
│       └─ Timeout: 120s                     │
│                                             │
│    c) onProgress(batchNum, totalBatches)   │
│       └─ UI progress update                │
│                                             │
│ 3. Return results + imageMapsBySegment     │
│                                             │
└────────────┬────────────────────────────────┘
             │
             ↓
┌───────────────────────────────────┐
│ AI Request Format (per batch)      │
│                                   │
│ POST /chat/completions            │
│ {                                 │
│   "model": "gpt-4o",              │
│   "temperature": 0.3,             │
│   "messages": [                   │
│     {                             │
│       "role": "system",           │
│       "content": "[preset prompt]"│
│     },                            │
│     {                             │
│       "role": "user",             │
│       "content": JSON.stringify([  │
│         {                         │
│           "context": "...",       │  (300 chars)
│           "candidates": [         │
│             {                     │
│               "id": "pexels:xxx", │
│               "semantic": "..."   │
│             },                    │
│             ...                   │
│           ]                       │
│         },                        │
│         ... (up to 10 segments)   │
│       ])                          │
│     }                             │
│   ]                               │
│ }                                 │
│                                   │
└────────────┬────────────────────┘
             │
             ↓
┌────────────────────────────────────┐
│ AI Response Format                 │
│                                    │
│ [                                  │
│   {                                │
│     "chosen": "pexels:123456",     │
│     "backups": [                   │
│       "pixabay:789012",            │
│       "unsplash:abc123"            │
│     ],                             │
│     "reason": "High-quality photo..│
│   },                               │
│   ... (10 results for 10 segments) │
│ ]                                  │
│                                    │
└────────────┬────────────────────┘
             │
             ↓
┌────────────────────────────┐
│ Validate & Retry          │
│ ├─ Parse JSON             │
│ ├─ Check chosen in candidates
│ ├─ Detect model errors    │
│ └─ Retry if incomplete    │
└────────────┬───────────────┘
             │
             ↓
     Images Selected!
```

---

### **Stage 5: Manual Override & User Selection** (Background Page UI)

```
User sees AI selected images
    ↓
┌─────────────────────────────┐
│ User Options:               │
│ A) Accept AI selection      │
│ B) Click backup image       │
│ C) Search manually          │
│ D) Re-run AI selection      │
└────────────┬────────────────┘
             │
     ┌───────┴───────┬──────────┬──────────┐
     │               │          │          │
     ↓               ↓          ↓          ↓
  (A) Accept    (B) Backup (C) Manual   (D) Re-run
     │               │          │          │
┌────┴───────────────┴──────────┴──────────┴────┐
│ BackgroundSelectionPanel.handleSelect()       │
│ ├─ Update selectedBackgrounds Map             │
│ ├─ Save to localStorage                       │
│ ├─ Call onBackgroundSelect callback           │
│ └─ Optionally show: "Image saved!"            │
└──┬─────────────────────────────────────────────┘
   │
   ├─ Prefetch image (non-blocking)
   │  └─ POST /api/image-prefetch
   │
   ↓
(Manual Search):
├─ Edit query in search input
├─ Call searchImages() with custom query
├─ Display new candidates
└─ Click to select

(Re-run AI):
├─ Click "Select with AI" button again
├─ Re-fetch candidates for selected segment
└─ Call AI again (usually gives same result, sometimes varies)
```

---

### **Stage 6: Metadata & Settings** (Before Save)

```
Before clicking "Next" button
    ↓
┌─────────────────────────────────────┐
│ Segment Selection Complete?         │
│ ├─ Check: selectedBackgrounds.size  │
│ ├─ Check: ≥ 1 image selected        │
│ └─ Error if none: "Chọn ít nhất..." │
└────────────┬────────────────────────┘
             │
             ✓ All segments have images
             │
             ↓
┌──────────────────────────────────────────┐
│ Visual Settings Configuration             │
│ ├─ Image Fit:                            │
│ │  └─ "square" | "full"                  │
│ ├─ Image Vertical Position:              │
│ │  └─ 0-100% (top to bottom)             │
│ ├─ Overlay Opacity:                      │
│ │  └─ 0-100% (transparency)              │
│ ├─ Motion Strength:                      │
│ │  └─ "low" | "medium" | "high"          │
│ └─ Save to localStorage                  │
└────────────┬─────────────────────────────┘
             │
             ↓
┌──────────────────────────────────────┐
│ Metadata for Each Selection           │
│ ├─ reason: "AI explanation"          │
│ ├─ backups: ["id1", "id2"]           │
│ └─ Save to localStorage:             │
│    podcast_selection_meta            │
└────────────┬───────────────────────┘
             │
             ↓
      Ready to Continue
```

---

### **Stage 7: Storage & Persistence** (Save to localStorage)

```
User clicks "Next" button
    ↓
┌──────────────────────────────────────┐
│ Save All Data to localStorage        │
│                                      │
│ 1. Copy segments                     │
│    podcast_ai_segments → podcast_script_segments
│                                      │
│ 2. Save selected images              │
│    podcast_selected_backgrounds      │
│    Format: {                         │
│      "segment-0": BackgroundImage,   │
│      "segment-1": BackgroundImage,   │
│      ...                             │
│    }                                 │
│                                      │
│ 3. Save metadata                     │
│    podcast_selection_meta            │
│    Format: {                         │
│      "segment-0": {                  │
│        reason: "...",                │
│        backups: ["id1", "id2"]       │
│      },                              │
│      ...                             │
│    }                                 │
│                                      │
│ 4. Save visual settings              │
│    podcast_background_image_fit      │
│    podcast_background_motion_strength│
│    podcast_background_overlay_opacity│
│    podcast_background_image_vertical_position
│                                      │
│ 5. Other metadata                    │
│    podcast_audio_duration            │
│    podcast_ai_model (used)           │
│    podcast_ai_analysis_preset_id     │
│    podcast_ai_image_selection_preset_id
│                                      │
└────────────┬─────────────────────────┘
             │
             ↓
     All Data Saved Successfully
```

---

### **Stage 8: Continue to Next Step** (Timeline Editor)

```
Saved to localStorage
    ↓
┌─────────────────────────────┐
│ handleNext() Callback       │
│ ├─ Validate saved data      │
│ ├─ Navigate to /config      │
│ └─ Timeline Editor loads    │
│    segments + backgrounds   │
└────────────┬────────────────┘
             │
             ↓
┌──────────────────────────────┐
│ /config (Config & Timeline)  │
│ ├─ Load podcast_script_segments
│ ├─ Load podcast_selected_backgrounds
│ ├─ Load podcast_selection_meta
│ ├─ Load visual settings      │
│ ├─ User edits segments       │
│ ├─ User adjusts background timing
│ └─ User re-exports/publishes │
└────────────┬─────────────────┘
             │
             ↓
      Next Steps: Video Export
```

---

## Service Architecture

### **Service Layer Diagram**

```
┌──────────────────────────────────────────────────────────────┐
│                      UI/Component Layer                      │
│                                                              │
│  Upload Page → AIAnalysisPanel → Background Page → Timeline  │
└────────────────────────────────────────────────────────────┬─┘
                                                              │
                                ┌─────────────────────────────┘
                                │
┌───────────────────────────────┴───────────────────────────┐
│                      Hook Layer                           │
│                                                          │
│  ├─ useAIAnalysis()           (Phase 1 orchestration)   │
│  │  └─ Manages: state, models, analysis trigger        │
│  │                                                      │
│  ├─ useAISelectBackgrounds()  (Phase 2 orchestration)   │
│  │  └─ Manages: state, progress, selection result      │
│  │                                                      │
│  └─ useImagePrefetch()        (Performance)             │
│     └─ Non-blocking image fetch                        │
└────────────────────────────────┬────────────────────────┘
                                │
┌───────────────────────────────┴────────────────────────┐
│              Service Layer                            │
│                                                       │
│  ├─ AIAnalysisService                               │
│  │  └─ Registry pattern for providers                │
│  │  └─ getProvider(name) → AIProv │ever             │
│  │                                 │
│  ├─ YesScaleProvider (AIProv │ever)
│  │  ├─ analyzeScript()              │
│  │  ├─ buildUserPrompt()            │
│  │  ├─ parseAIResponse()            │
│  │  ├─ fetchWithRetry()             │
│  │  ├─ getAvailableModels()         │
│  │  └─ validateApiKey()             │
│  │                                 │
│  ├─ selectBackgroundsService       │
│  │  ├─ getSearchQueryForSegment()   │
│  │  ├─ fetchCandidatesForSegment()  │
│  │  ├─ callAISelectImages()         │
│  │  ├─ runSelectBackgrounds()       │
│  │  └─ runWithConcurrency()         │
│  │                                 │
│  ├─ BackgroundManagerService       │
│  │  ├─ searchImages() (3 providers) │
│  │  └─ prefetchImage()              │
│  │                                 │
│  └─ Preset Services                │
│     ├─ getPresets()                │
│     ├─ savePreset()                │
│     ├─ deletePreset()              │
│     └─ getBuiltInDefaultPreset()   │
│                                    │
└─────────────────┬──────────────────┘
                  │
┌─────────────────┴──────────────────────────────────────┐
│            External API Layer                          │
│                                                        │
│  ├─ YesScale (OpenAI-compatible)                      │
│  │  └─ /chat/completions                             │
│  │  └─ /models                                       │
│  │                                                   │
│  ├─ Pexels API                                       │
│  │  └─ /search (photos)                              │
│  │                                                   │
│  ├─ Pixabay API                                      │
│  │  └─ /search (images)                              │
│  │                                                   │
│  ├─ Unsplash API                                     │
│  │  └─ /search/photos                                │
│  │                                                   │
│  └─ Browser APIs                                     │
│     ├─ localStorage (config + segments)              │
│     ├─ IndexedDB (large files: audio)                │
│     └─ Promise-based async/await                     │
└────────────────────────────────────────────────────────│
```

### **Service Responsibilities**

| Service | Responsibility | Key Methods | Used By |
|---------|---|---|---|
| **AIAnalysisService** | Provider registry & discovery | getProvider(), registerProvider() | useAIAnalysis hook |
| **YesScaleProvider** | Script analysis execution | analyzeScript(), buildUserPrompt(), parseAIResponse(), fetchWithRetry() | AIAnalysisService |
| **selectBackgroundsService** | Image selection orchestration | runSelectBackgrounds(), fetchCandidatesForSegment(), callAISelectImages() | useAISelectBackgrounds hook |
| **BackgroundManagerService** | Multi-provider image search | searchImages() (delegates to Pexels/Pixabay/Unsplash) | selectBackgroundsService |
| **Preset Services** | Configuration management | getPresets(), savePreset(), deletePreset() | Component UI, hooks |

---

## Data Models & Types

### **Phase 1: Script Analysis Types**

```typescript
// === INPUT ===
interface AIAnalysisRequest {
  script: string              // Podcast script (Vietnamese/any language)
  audioDuration: number       // Duration in seconds (e.g., 3600)
  model?: string              // Model ID (default: gpt-4o)
  temperature?: number        // 0-2 (default: 0.7)
  maxTokens?: number          // Default: 8192
  systemPrompt?: string       // Use default if not provided
}

// === PROCESSING ===
interface AIProvider {
  analyzeScript(request: AIAnalysisRequest, config: APIConfig): Promise<AIAnalysisResponse>
  buildUserPrompt(script: string, duration: number): string
  parseAIResponse(response: string, duration: number): AISegment[]
  getAvailableModels(config: APIConfig): Promise<Model[]>
  validateApiKey(config: APIConfig): Promise<boolean>
}

// === OUTPUT ===
interface AIAnalysisResponse {
  segments: AISegment[]
  rawResponse: string
}

interface AISegment {
  startTime: number          // e.g., 0.0
  endTime: number            // e.g., 5.58
  text: string               // Segment content
  keywords: string[]         // [5 English keywords]
  description: string        // Brief summary
  suggestedBackgrounds?: string
}

interface APIConfig {
  apiKey: string
  baseUrl?: string
  timeout?: number
  model?: string
  temperature?: number
}
```

### **Phase 2: Image Selection Types**

```typescript
// === INPUT ===
interface AIImageSelectionRequest {
  segments: AISegment[]                    // From Phase 1
  preset: AIImageSelectionPreset
  apiKey: string
  model?: string                           // Default from preset
  temperature?: number                     // Default: 0.3 (deterministic)
}

interface AIImageSelectionPreset {
  id: string
  name: string
  systemPrompt: string
  model: string
  temperature: number
  maxTokens: number
  language?: 'en' | 'vi'
  builtIn: boolean
}

// === PROCESSING ===
interface ImageCandidate {
  id: string                 // "source:provider_id"
  semantic: string           // Content description
  source: 'pexels' | 'pixabay' | 'unsplash'
}

interface BackgroundImage {
  id: string
  source: 'pexels' | 'pixabay' | 'unsplash'
  url: string                // Display URL
  downloadUrl?: string       // High-res download
  description: string
  tags?: string[]
  width: number
  height: number
  photographer?: string
  attributionUrl?: string
}

// === OUTPUT ===
interface AIImageSelectionResultItem {
  segmentIndex: number
  segmentId: string
  chosenId: string           // Image ID selected
  backups: string[]          // Alternative image IDs [2-3]
  reason: string             // Vietnamese explanation
}

interface AIImageSelectionResponse {
  results: AIImageSelectionResultItem[]
  imageMapsBySegment: Map<number, Map<string, BackgroundImage>>
}

// === STORAGE ===
interface AISelectionMeta {
  reason: string             // Why AI chose this image
  backups: string[]          // Backup image IDs
}

interface SelectedBackgroundMap {
  [segmentId: string]: BackgroundImage
}
```

### **UI & Hook Types**

```typescript
// useAIAnalysis hook
interface UseAIAnalysisState {
  loading: boolean
  error: Error | null
  models: Model[]
  result: AIAnalysisResponse | null
}

interface UseAIAnalysisActions {
  analyzeScript(request: AIAnalysisRequest, config: APIConfig): Promise<AIAnalysisResponse | null>
  validateApiKey(apiKey: string): Promise<boolean>
  getModels(config: APIConfig): Promise<Model[]>
  clearError(): void
}

// useAISelectBackgrounds hook
interface UseAISelectBackgroundsState {
  loading: boolean
  progress: number          // 0-100
  error: Error | null
}

interface UseAISelectBackgroundsActions {
  run(segments: AISegment[]): Promise<AIImageSelectionResponse | null>
  clearError(): void
}

interface UseAISelectBackgroundsConfig {
  preset: AIImageSelectionPreset | string  // ID or preset object
  onSelect?: (segmentId: string, image: BackgroundImage) => void
  onSelectionMeta?: (segmentId: string, meta: AISelectionMeta) => void
  getSelectedBackgrounds?: () => SelectedBackgroundMap
}
```

---

## Module Structure

### **File Organization**

```
project-root/
├── modules/
│   ├── ai-analysis/
│   │   ├── types.ts                           (AISegment, AIProvider, etc.)
│   │   ├── presets.ts                         (System prompts, DEFAULT_SYSTEM_PROMPT)
│   │   ├── service.ts                         (AIAnalysisService - registry)
│   │   ├── providers/
│   │   │   └── YesScaleProvider.ts           (Main implementation)
│   │   └── useAIAnalysis.ts                   (React hook)
│   │
│   └── ai-image-selection/
│       ├── types.ts                           (ImageCandidate, AIImageSelectionResultItem)
│       ├── presets.ts                         (Image selection presets & prompts)
│       ├── selectBackgroundsService.ts        (Batch processing + AI selection)
│       ├── BackgroundManagerService.ts        (Multi-provider search)
│       ├── providers/
│       │   ├── PexelsProvider.ts              (Pexels API)
│       │   ├── PixabayProvider.ts             (Pixabay API)
│       │   └── UnsplashProvider.ts            (Unsplash API)
│       └── useAISelectBackgrounds.ts          (React hook with retry)
│
├── components/
│   ├── ui/
│   │   ├── AIAnalysisPanel.tsx                (Phase 1 UI - modal/panel)
│   │   ├── BackgroundSelectionPanel.tsx       (Phase 2 UI - main page)
│   │   ├── SegmentList.tsx                    (Segment sidebar)
│   │   └── SegmentDetails.tsx                 (Details + image gallery)
│   │
│   ├── editor/
│   │   └── (Timeline, video editing components)
│   │
│   └── video/ (Remotion components)
│
├── app/
│   ├── upload/
│   │   └── page.tsx                          (Upload & analyze)
│   │
│   ├── background/
│   │   └── page.tsx                          (Image selection)
│   │
│   ├── config/
│   │   └── page.tsx                          (Timeline editor)
│   │
│   └── api/
│       ├── image-prefetch/
│       │   └── route.ts                       (Non-blocking image fetch)
│       └── (other API routes)
│
├── lib/
│   ├── ai/
│   │   ├── storage.ts                         (localStorage helpers)
│   │   └── utils.ts                           (Common utilities)
│   │
│   └── (other utilities)
│
└── public/ (assets, fonts, etc.)
```

---

## Integration Points

### **How Components Communicate**

```
┌─────────────────────────────┐
│  Upload Page (page.tsx)     │
├─────────────────────────────┤
│ 1. User enters script       │
│ 2. Uploads audio            │
│ 3. Validates inputs         │
│ 4. Shows AIAnalysisPanel    │
└────────────┬────────────────┘
             │ Passes: {script, audioDuration, model, apiKey}
             ↓
┌─────────────────────────────┐
│  AIAnalysisPanel (modal)    │
├─────────────────────────────┤
│ • Shows progress (0-100%)   │
│ • Calls useAIAnalysis hook  │
│ • On success: Navigate to   │
│   /background               │
└────────────┬────────────────┘
             │ useAIAnalysis
             │ ├─ Validates api key
             │ ├─ Fetches models
             │ └─ Calls YesScaleProvider
             │
             ↓
┌─────────────────────────────┐
│  Background Page            │
├─────────────────────────────┤
│ 1. Load segments from       │
│    localStorage             │
│ 2. Load saved images        │
│ 3. Show BackgroundSelection │
│    Panel                    │
└────────────┬────────────────┘
             │ Passes: {
             │   aiSegments,
             │   selectedBackgrounds,
             │   onBackgroundSelect,     // save to loc
             │   onSelectionMeta,         // save reason
             │   onNext,                  // navigate /config
             │   onBack                   // back
             │ }
             ↓
┌──────────────────────────────────┐
│ BackgroundSelectionPanel         │
├──────────────────────────────────┤
│ • Left: SegmentList              │
│ • Right: Details + Image Gallery │
│ • AI Controls button             │
│ • Calls useAISelectBackgrounds   │
│   when user clicks "Select with AI"
└────────────┬─────────────────────┘
             │ useAISelectBackgrounds
             │ ├─ Validates preset
             │ ├─ Calls selectBgService
             │ │  ├─ Fetches candidates
             │ │  ├─ Batches (10/batch)
             │ │  └─ Calls AI
             │ └─ Manages retries
             │
             ↓
┌──────────────────────────────────┐
│ Timeline Editor (/config)        │
├──────────────────────────────────┤
│ • Load segments + images         │
│ • User edits segments/timing     │
│ • Export to video                │
└──────────────────────────────────┘
```

### **localStorage Communication Map**

```
Upload Page Writes:
  podcast_script
  podcast_audio_duration
  podcast_audio_blob (optional)

AIAnalysisPanel Writes:
  podcast_ai_segments            [{startTime, endTime, text, keywords[], description}]
  podcast_ai_analysis_result     {segments, rawResponse}
  podcast_ai_model               "gpt-4o"  (used model)
  podcast_ai_analysis_preset_id  "default"

Background Page Reads:
  podcast_ai_segments            ← For display + context
  podcast_audio_duration         ← For validation

Background Page Writes:
  podcast_selected_backgrounds   {segmentId: BackgroundImage}
  podcast_selection_meta         {segmentId: {reason, backups}}
  podcast_background_image_fit   "square" | "full"
  podcast_background_motion_strength  "low" | "medium" | "high"
  podcast_background_overlay_opacity  (0-100)
  podcast_background_image_vertical_position  (0-100)

Timeline Editor Reads:
  podcast_script_segments        ← Copy of podcast_ai_segments
  podcast_selected_backgrounds
  podcast_selection_meta
  All visual settings
```

---

## Provider Architecture (SPI Pattern)

### **Service Provider Interface (SPI)**

The `AIProvider` interface allows pluggable implementations:

```typescript
// lib/ai-analysis/types.ts
export interface AIProvider {
  /**
   * Analyze a podcast script and extract segments
   */
  analyzeScript(
    request: AIAnalysisRequest,
    config: APIConfig
  ): Promise<AIAnalysisResponse>;

  /**
   * Build the user-facing prompt for analysis
   */
  buildUserPrompt(script: string, audioDuration: number): string;

  /**
   * Parse AI response into structured segments
   */
  parseAIResponse(
    response: string,
    audioDuration: number
  ): AISegment[];

  /**
   * Get available models from provider
   */
  getAvailableModels(config: APIConfig): Promise<Model[]>;

  /**
   * Validate API key without making a full request
   */
  validateApiKey(config: APIConfig): Promise<boolean>;
}
```

### **Registry Pattern**

```typescript
// lib/ai-analysis/service.ts
export class AIAnalysisService {
  private static providers: Map<string, AIProvider> = new Map();

  static registerProvider(provider: AIProvider): void {
    this.providers.set(provider.name, provider);
  }

  static getProvider(name: string = 'yescale'): AIProvider {
    const provider = this.providers.get(name);
    if (!provider) {
      throw new Error(`Provider "${name}" not registered`);
    }
    return provider;
  }

  static async analyzeScript(
    request: AIAnalysisRequest,
    config: APIConfig,
    providerName: string = 'yescale'
  ): Promise<AIAnalysisResponse> {
    const provider = this.getProvider(providerName);
    return provider.analyzeScript(request, config);
  }
}

// Auto-register YesScale on module load
YesScaleProvider.register();
```

### **Current Implementation**

```
┌─────────────────────────────────────┐
│        AIAnalysisService            │
│        (Registry)                   │
│                                     │
│  getProvider('yescale')             │
│  registerProvider(provider)         │
│  analyzeScript(request)             │
└──────────────┬──────────────────────┘
               │
        Can support multiple providers:
        ├─ YesScaleProvider (current)
        ├─ OpenAIProvider (future)
        ├─ ClaudeProvider (future)
        └─ LocalProvider (future)
```

---

## Storage & Persistence

### **Data Persistence Model**

```
┌──────────────────────────────────────────┐
│         Browser Storage Hierarchy         │
├──────────────────────────────────────────┤
│                                          │
│  Session Cache (RAM)                     │
│  ├─ useAIAnalysis hook state            │
│  ├─ useAISelectBackgrounds hook state   │
│  └─ Component local state               │
│                                          │
│  ↓ (Persist to localStorage)             │
│                                          │
│  localStorage (5-10 MB per origin)       │
│  ├─ podcast_script (text)               │
│  ├─ podcast_audio_duration (number)     │
│  ├─ podcast_ai_segments (JSON)          │
│  ├─ podcast_ai_analysis_result (JSON)   │
│  ├─ podcast_selected_backgrounds (JSON) │
│  ├─ podcast_selection_meta (JSON)       │
│  ├─ Visual settings (multiple keys)     │
│  ├─ Preset configurations (JSON)        │
│  └─ User preferences (JSON)             │
│                                          │
│  ↓ (For large files: >8 MB)              │
│                                          │
│  IndexedDB (Quota: 50+ MB per origin)    │
│  ├─ podcast_audio_blob (Blob/File)      │
│  ├─ podcast_audio_file_name (string)    │
│  └─ Cached images (future optimization) │
│                                          │
└──────────────────────────────────────────┘
```

### **Lifecycle of Data**

```
1. INPUT PHASE (Upload Page)
   User enters script → podcast_script (localStorage)
   User uploads audio → podcast_audio_duration (localStorage)
                     → podcast_audio_blob? (IndexedDB if >8MB)

2. ANALYSIS PHASE (AIAnalysisPanel)
   API response → Parse → podcast_ai_segments (localStorage)
                       → podcast_ai_analysis_result (localStorage)
                       → podcast_ai_model (localStorage)

3. BACKGROUND PHASE (Background Page)
   Load segments  → podcast_ai_segments (read from localStorage)
   User selects   → podcast_selected_backgrounds (save)
                 → podcast_selection_meta (save)
                 → Visual settings (save)

4. CONFIG PHASE (Timeline Editor)
   Load all data  → podcast_script_segments (copy from podcast_ai_segments)
                 → podcast_selected_backgrounds (read)
                 → podcast_selection_meta (read)
                 → podcast_background_* (read)

5. EXPORT PHASE
   All data → Video file (new Blob in IndexedDB or downloads folder)
```

### **Storage Keys Reference**

| Key | Type | Size | Used By | Lifecycle |
|-----|------|------|---------|-----------|
| `podcast_script` | string | <1 MB | Upload, Analysis | Created → Deleted after export |
| `podcast_audio_duration` | string (serialized number) | <100 B | All pages | Persistent |
| `podcast_audio_blob` | Blob (IndexedDB) | 10-200 MB | Upload, Timeline | Optional, large files |
| `podcast_ai_segments` | JSON string (AISegment[]) | 10-100 KB | Background, Timeline | Created by analysis |
| `podcast_ai_analysis_result` | JSON string | 10-100 KB | Debug, reference | Optional backup |
| `podcast_ai_model` | string | <50 B | Config | For reference |
| `podcast_ai_analysis_preset_id` | string | <50 B | UI state | User preference |
| `podcast_selected_backgrounds` | JSON (Map) | 50-200 KB | All pages | Until export |
| `podcast_selection_meta` | JSON (Map) | 10-50 KB | All pages | Until export |
| `podcast_background_image_fit` | string | <20 B | Background, Timeline | User preference |
| `podcast_background_image_vertical_position` | string | <20 B | Timeline | User preference |
| `podcast_background_overlay_opacity` | string | <20 B | Timeline | User preference |
| `podcast_background_motion_strength` | string | <20 B | Timeline | User preference |

---

## Error Handling Strategy

### **Error Categories & Recovery**

```
┌───────────────────────────────────────────────────────────┐
│              ERROR CLASSIFICATION                         │
├───────────────────────────────────────────────────────────┤
│                                                           │
│ 1. VALIDATION ERRORS (User Input)                         │
│   ├─ Empty script                                         │
│   ├─ Invalid audio file                                  │
│   ├─ Missing API key                                     │
│   └─ Recovery: Show user-friendly error message          │
│                                                           │
│ 2. API ERRORS (Network/Service)                          │
│   ├─ 401 Unauthorized (invalid API key)                  │
│   ├─ 429 Too Many Requests (rate limited)                │
│   ├─ 503 Service Unavailable (model down)                │
│   ├─ Network timeout (>90s for analysis, >120s for images)
│   └─ Recovery: Automatic retry with backoff              │
│                                                           │
│ 3. PARSING ERRORS (Response Format)                      │
│   ├─ Invalid JSON response                               │
│   ├─ Missing required fields                             │
│   ├─ Invalid timestamps                                  │
│   └─ Recovery: Repair or show error                      │
│                                                           │
│ 4. EXTERNAL SERVICE ERRORS (Image Providers)             │
│   ├─ Pexels API key invalid                             │
│   ├─ Pixabay rate limited                               │
│   ├─ Unsplash quota exceeded                            │
│   └─ Recovery: Try fallback provider, cache results      │
│                                                           │
│ 5. BUSINESS LOGIC ERRORS (Data Consistency)              │
│   ├─ Segments not contiguous (timestamp gaps)            │
│   ├─ Keywords count ≠ 5                                 │
│   ├─ Chosen image not in candidates                      │
│   └─ Recovery: Auto-repair or manual intervention        │
│                                                           │
└───────────────────────────────────────────────────────────┘
```

### **Retry Strategies**

```
┌─────────────────────────────────────────────────┐
│             RETRY CONFIGURATION                 │
├─────────────────────────────────────────────────┤
│                                                 │
│ API Key Validation: 1 attempt (no retry)        │
│                                                 │
│ Model Availability: 3 attempts                  │
│   - Delay: 1s, 2s, 4s                          │
│   - Exponential backoff                        │
│                                                 │
│ Script Analysis: 1-3 attempts                   │
│   - Delay: 1s, 2s, 4s                          │
│   - Timeout per attempt: 90s                   │
│   - Recoverable: timeout, transient 5xx errors │
│                                                 │
│ Image Candidate Fetch: 1 attempt per provider  │
│   - Use Promise.allSettled() for robustness    │
│   - Fall through to next provider if one fails │
│                                                 │
│ Image Selection (Batch): 1-2 attempts          │
│   - Delay: 1s, 2s                              │
│   - Timeout: 120s per batch                    │
│   - Retry on: timeout, 429, 5xx errors        │
│                                                 │
│ Missing Images (After AI): Up to 5 attempts     │
│   - Exponential backoff: 1s, 2s, 4s, 8s, 16s  │
│   - Re-fetch candidates + re-run AI for missing
│   - Stop when: 5 attempts exhausted or success │
│                                                 │
└─────────────────────────────────────────────────┘
```

---

## Performance Considerations

### **Optimization Strategies**

```
┌──────────────────────────────────────────────────────────┐
│              PERFORMANCE OPTIMIZATION                    │
├──────────────────────────────────────────────────────────┤
│                                                          │
│ 1. BATCHING (Image Selection)                           │
│    ├─ Segments per batch: 10                            │
│    ├─ Max concurrent batches: 3                         │
│    ├─ Benefit: Reduce API calls, efficient processing  │
│    └─ Trade-off: Longer initial wait for large content │
│                                                          │
│ 2. PARALLEL PROCESSING (Image Providers)                │
│    ├─ Fetch from Pexels + Pixabay + Unsplash in parallel
│    ├─ Use Promise.allSettled() for robustness         │
│    ├─ Benefit: ~3x faster candidate pool generation   │
│    └─ Trade-off: API rate limit consumption            │
│                                                          │
│ 3. CACHING (Presets)                                   │
│    ├─ Cache system prompts in localStorage             │
│    ├─ Cache available models                           │
│    ├─ Benefit: Instant preset loading                  │
│    └─ Trade-off: Slight storage usage (~10 KB)         │
│                                                          │
│ 4. SELECTIVE PREFETCH (Images)                         │
│    ├─ Prefetch selected image on selection            │
│    ├─ Non-blocking (POST /api/image-prefetch)         │
│    ├─ Benefit: Faster display when scrolling          │
│    └─ Trade-off: Extra network request per image      │
│                                                          │
│ 5. LAZY LOADING (Components)                           │
│    ├─ Load image candidates only on segment selection │
│    ├─ Benefit: Faster initial page load               │
│    └─ Trade-off: User waits when clicking segment     │
│                                                          │
│ 6. COMPRESSION (Data)                                  │
│    ├─ API responses gzip-encoded                      │
│    ├─ localStorage JSON encoded (no binary)           │
│    ├─ Benefit: Reduce bandwidth                       │
│    └─ Trade-off: Slight CPU overhead                  │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

### **Rate Limiting Compliance**

```
Provider Rate Limits & Strategy:

PEXELS (200 requests/hour)
├─ Query strategy: 1 search query per segment
├─ Batch size: 10 segments (10 requests per batch)
├─ Concurrency: Max 3 concurrent batches (30 req/min)
└─ Safety margin: ~60% capacity used

PIXABAY (5,000 requests/hour)
├─ Most generous, used as primary
├─ Same batch strategy as Pexels
└─ Safety: <10% capacity used at max load

UNSPLASH (50 requests/hour)
├─ Most restrictive
├─ Used sparingly or request higher access
├─ Fallback strategy recommended
└─ Needs: Pre-caching or separate API plan

YesScale (Model rate limiting depends on plan)
├─ Batch analysis: 10 segments per call
├─ Token limits enforced by model
├─ Monitor usage dashboard
└─ Upgrade plan if hitting limits
```

### **Timeout Configuration**

```
Operation Timeouts:

Get API Key (validation):     30 seconds
Fetch Models (startup):       30 seconds
Analyze Script (Phase 1):     90 seconds  (±10% for network)
Fetch Image Candidates:       10 seconds  (per provider, parallel)
Select Images with AI (batch):120 seconds (for 10 segments)

Total User Experience:
- Script analysis end-to-end: ~100 seconds
- Image selection per batch: ~120 seconds
- Manual search + select: ~5 seconds
```

---

## Summary

This architecture provides:

✅ **Clean separation of concerns** - Layers: UI → Hooks → Services → APIs
✅ **Extensibility** - SPI pattern allows adding new AI providers
✅ **Robustness** - Comprehensive error handling + automatic retry logic
✅ **Performance** - Batching, parallel processing, caching strategies
✅ **Type safety** - TypeScript interfaces for all data structures
✅ **User experience** - Progress indication, preset management, manual override
✅ **Persistence** - localStorage + IndexedDB for reliable data recovery

The system handles both happy paths and edge cases with graceful degradation, making it production-ready for podcast AI analysis and background image selection.
