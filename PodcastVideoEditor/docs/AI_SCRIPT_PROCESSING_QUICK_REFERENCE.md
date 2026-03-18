# 🚀 AI Script Processing - Quick Reference Guide

**For rapid understanding and migration**

---

## ⚡ Quick Start (30 seconds)

```
PHASE 1 - SCRIPT ANALYSIS:
INPUT:  script text + audio file → User clicks "Phân tích AI"
        ↓
PROCESS: AIAnalysisPanel → useAIAnalysis → YesScaleProvider
        ↓  
OUTPUT: AISegment[] array → saved to localStorage
        ↓

PHASE 2 - IMAGE SELECTION:
INPUT:  AISegment[] (with keywords)
        ↓
PROCESS: useAISelectBackgrounds → selectBackgroundsService → Fetch candidates → AI selects
        ↓
OUTPUT: Chosen image + backups per segment
        ↓
NEXT:   Video creation with selected backgrounds + segments
```

---

## 🎯 Key Files (Copy These!)

### **Phase 1: Script Analysis**
| File | Purpose | Lines | Must-Have |
|------|---------|-------|-----------|
| `modules/ai-analysis/AIAnalysisService.ts` | Service registry + provider pattern | ~100 | ✅ CRITICAL |
| `modules/ai-analysis/providers/YesScaleProvider.ts` | Main AI logic + API calls | ~400 | ✅ CRITICAL |
| `modules/ai-analysis/useAIAnalysis.ts` | React hook for state + operations | ~150 | ✅ CRITICAL |
| `lib/ai/types.ts` | TypeScript interfaces | ~100 | ✅ CRITICAL |
| `lib/ai-analysis/presets.ts` | System prompts + defaults | ~100 | ✅ CRITICAL |
| `components/ui/AIAnalysisPanel.tsx` | UI + progress display | ~400 | ⭐ Optional (UI) |

### **Phase 2: Image Selection**
| File | Purpose | Lines | Must-Have |
|------|---------|-------|-----------|
| `modules/ai-image-selection/selectBackgroundsService.ts` | Image selection logic | ~300 | ✅ CRITICAL |
| `modules/ai-image-selection/useAISelectBackgrounds.ts` | React hook | ~150 | ✅ CRITICAL |
| `lib/ai-image-selection/presets.ts` | Image selection prompts | ~80 | ✅ CRITICAL |
| `lib/ai-image-selection/types.ts` | Type definitions | ~50 | ✅ CRITICAL |
| `modules/background-manager/BackgroundManagerService.ts` | Image provider search | ~200 | ✅ CRITICAL |
| `lib/background/storage.ts` | API key management | ~50 | ✅ CRITICAL |
| `app/upload/page.tsx` | Integration point | ~800 | 📖 Reference |

---

## 🔌 Three-Step Integration

### **Phase 1: Set Up Script Analysis Services**
```typescript
// In your app initialization
import { getAIAnalysisService } from '@/modules/ai-analysis/AIAnalysisService';

const aiService = getAIAnalysisService();
// Ready to use! Singleton already registered YesScaleProvider
```

### **Phase 2: Use Script Analysis Hook in Component**
```typescript
import { useAIAnalysis } from '@/modules/ai-analysis/useAIAnalysis';

export function YourComponent() {
  const { loading, error, analyzeScript } = useAIAnalysis('yescale');

  const handleAnalyze = async () => {
    const result = await analyzeScript(
      {
        script: 'user script text...',
        audioDuration: 3600,
        model: 'gpt-4o',
        systemPrompt: customPrompt, // optional
      },
      { apiKey: 'your-api-key' }
    );

    if (result) {
      console.log(result.segments);  // AISegment[]
      // Continue to Phase 3
    }
  };
}
```

### **Phase 3: Use Image Selection Hook**
```typescript
import { useAISelectBackgrounds } from '@/modules/ai-image-selection/useAISelectBackgrounds';

export function BackgroundSelector({ segments }) {
  const { run: selectImages, loading, progress, error } = useAISelectBackgrounds({
    aiSegments: segments,  // From Phase 2
    preset,                // Image selection preset
    onSelect: (segmentId, image) => {
      // Handle selected image for segment
      setBackgrounds(prev => ({ ...prev, [segmentId]: image }));
    },
    onSelectionMeta: (segmentId, { reason, backups }) => {
      // Save reason + backups for reference/manual override
    },
  });

  return (
    <button onClick={selectImages} disabled={loading}>
      {loading ? `Selecting images (${progress})...` : 'Select with AI'}
    </button>
  );
}
```

---

## ⚙️ Initialization & Configuration Guide

### **Step 1: Set Up Environment Variables**

Create `.env.local` in your project root:

```bash
# YesScale Configuration
VITE_YESCALE_API_KEY=sk_live_xxxxxxxxxxxxxxxxxxxxx
VITE_YESCALE_BASE_URL=https://api.yescale.vip/v1
VITE_YESCALE_DEFAULT_MODEL=gpt-4o

# Image Provider APIs
VITE_PEXELS_API_KEY=xxxxxxxxxxxxxxxxxxxxx
VITE_PIXABAY_API_KEY=xxxxxxxxxxxxxxxxxxxxx
VITE_UNSPLASH_API_KEY=xxxxxxxxxxxxxxxxxxxxx

# Optional Timeouts (milliseconds)
VITE_YESCALE_TIMEOUT=90000        # Script analysis
VITE_IMAGE_TIMEOUT=120000         # Image selection batch
VITE_ANALYSIS_VALIDATE_TIMEOUT=30000     # Quick API checks
```

### **Step 2: Initialize AI Services (App Startup)**

```typescript
// app/layout.tsx or main app initialization

import { getAIAnalysisService } from '@/modules/ai-analysis/AIAnalysisService';

export default async function RootLayout() {
  // Services auto-register on first access - no explicit init needed!
  // YesScaleProvider automatically registers with AIAnalysisService
  
  // Optional: Pre-fetch available models on app startup
  const service = getAIAnalysisService();
  try {
    const models = await service.getAvailableModels({
      apiKey: process.env.VITE_YESCALE_API_KEY || '',
    });
    console.log('Available models:', models);
  } catch (err) {
    console.warn('Could not pre-fetch models:', err);
    // Continue - models will be fetched on-demand
  }

  return (
    <html>
      <body>{children}</body>
    </html>
  );
}
```

### **Step 3: Configure Hook Usage in Components**

```typescript
// Component: pages/script-analysis.tsx

'use client';

import { useAIAnalysis } from '@/modules/ai-analysis/useAIAnalysis';
import { useAISelectBackgrounds } from '@/modules/ai-image-selection/useAISelectBackgrounds';
import { useState } from 'react';

export default function ScriptAnalysisPage() {
  const [segments, setSegments] = useState([]);
  
  // PHASE 1: Script Analysis
  const {
    loading: analyzing,
    error: analysisError,
    models,
    analyzeScript,
  } = useAIAnalysis('yescale');

  // PHASE 2: Image Selection
  const {
    loading: selectingImages,
    progress: imageProgress,
    error: imageError,
    run: selectBackgrounds,
  } = useAISelectBackgrounds({
    preset: 'default',  // From lib/ai-image-selection/presets
    onSelect: (segmentId, image) => {
      console.log(`Selected for ${segmentId}:`, image);
    },
    onSelectionMeta: (segmentId, meta) => {
      console.log(`Reason for ${segmentId}:`, meta.reason);
    },
  });

  const handleAnalyze = async (script: string, duration: number) => {
    const result = await analyzeScript(
      {
        script,
        model: models?.[0]?.id || 'gpt-4o',
        audioDuration: duration,
      },
      { apiKey: process.env.VITE_YESCALE_API_KEY || '' }
    );

    if (result) {
      setSegments(result.segments);
      // Auto-start image selection
      await selectBackgrounds(result.segments);
    }
  };

  return (
    <div>
      <button onClick={() => handleAnalyze(script, 3600)}>
        {analyzing ? 'Analyzing...' : 'Analyze Script'}
      </button>
      {analysisError && <div className="error">{analysisError.message}</div>}
      
      {selectingImages && (
        <div className="progress">Selecting images... {imageProgress}%</div>
      )}
      {imageError && <div className="error">{imageError.message}</div>}
    </div>
  );
}
```

### **Step 4: Validate Configuration Before Use**

```typescript
// Validation helper

export async function validateConfiguration() {
  const errors: string[] = [];
  
  // Check API keys
  if (!process.env.VITE_YESCALE_API_KEY) {
    errors.push('Missing VITE_YESCALE_API_KEY');
  }
  
  // Check image provider keys
  const providers = ['pexels', 'pixabay', 'unsplash'];
  const missingProviders = providers.filter(
    p => !process.env[`VITE_${p.toUpperCase()}_API_KEY`]
  );
  if (missingProviders.length === 3) {
    errors.push('At least one image provider API key required');
  }

  // Test YesScale connection
  if (process.env.VITE_YESCALE_API_KEY) {
    try {
      const service = getAIAnalysisService();
      const valid = await service.validateApiKey({
        apiKey: process.env.VITE_YESCALE_API_KEY,
      });
      if (!valid) {
        errors.push('YesScale API key invalid');
      }
    } catch (err) {
      errors.push(`YesScale connection failed: ${err}`);
    }
  }

  return { valid: errors.length === 0, errors };
}
```

### **Step 5: Persistent Storage Configuration**

```typescript
// lib/ai/storage.ts

const STORAGE_KEYS = {
  API_KEY: 'podcast_ai_api_key',
  SELECTED_MODEL: 'podcast_ai_model',
  SEGMENTS: 'podcast_ai_segments',
  PRESET_ID: 'podcast_ai_analysis_preset_id',
  IMAGE_PRESET_ID: 'podcast_ai_image_selection_preset_id',
};

export function saveConfiguration(config: {
  apiKey: string;
  model: string;
  analyzePresetId?: string;
  imagePresetId?: string;
}) {
  if (typeof window !== 'undefined') {
    localStorage.setItem(STORAGE_KEYS.API_KEY, config.apiKey);
    localStorage.setItem(STORAGE_KEYS.SELECTED_MODEL, config.model);
    if (config.analyzePresetId) {
      localStorage.setItem(STORAGE_KEYS.PRESET_ID, config.analyzePresetId);
    }
    if (config.imagePresetId) {
      localStorage.setItem(STORAGE_KEYS.IMAGE_PRESET_ID, config.imagePresetId);
    }
  }
}

export function loadConfiguration() {
  if (typeof window === 'undefined') return null;
  return {
    apiKey: localStorage.getItem(STORAGE_KEYS.API_KEY),
    model: localStorage.getItem(STORAGE_KEYS.SELECTED_MODEL),
    analyzePresetId: localStorage.getItem(STORAGE_KEYS.PRESET_ID),
    imagePresetId: localStorage.getItem(STORAGE_KEYS.IMAGE_PRESET_ID),
  };
}
```

---

## 📊 Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│ INPUT: AIAnalysisRequest                                    │
├─────────────────────────────────────────────────────────────┤
│ {                                                           │
│   script: "podcast script text...",                        │
│   audioDuration: 3600,                                     │
│   model: "gpt-4o",                                         │
│   temperature: 0.7,                                        │
│   maxTokens: 8192,                                         │
│   systemPrompt?: "custom prompt or use default"            │
│ }                                                           │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ↓
        ┌────────────────────────────────────┐
        │  YesScaleProvider.analyzeScript()  │
        │  ├─ buildUserPrompt()              │
        │  ├─ Call API                       │
        │  └─ parseAIResponse()              │
        └────────────────────┬───────────────┘
                             │
                ┌────────────┴────────────┐
                │ API Request             │
                ├────────────────────────┤
                │ POST /chat/completions │
                │ model: gpt-4o          │
                │ messages: [system, user]│
                │ temperature: 0.7       │
                │ max_tokens: 8192       │
                └────────────┬───────────┘
                             │
                ┌────────────┴────────────┐
                │ API Response (JSON)     │
                │ [                       │
                │   {                     │
                │     "startTime": 0,     │
                │     "endTime": 5.58,    │
                │     "text": "...",      │
                │     "keywords": [...]   │
                │   },                    │
                │   ...                   │
                │ ]                       │
                └────────────┬───────────┘
                             │
                             ↓
        ┌────────────────────────────────────┐
        │ Parse & Validate                   │
        │ ├─ Parse JSON                      │
        │ ├─ Validate timestamps (contiguous)│
        │ ├─ Validate keywords (5 english)  │
        │ ├─ Repair empty text              │
        │ └─ Return AISegment[]             │
        └────────────────────┬───────────────┘
                             │
                             ↓
┌─────────────────────────────────────────────────────────────┐
│ OUTPUT: AIAnalysisResponse                                  │
├─────────────────────────────────────────────────────────────┤
│ {                                                           │
│   segments: [                                              │
│     {                                                      │
│       startTime: 0,                                        │
│       endTime: 5.58,                                       │
│       text: "segment content",                            │
│       keywords: ["kw1", "kw2", "kw3", "kw4", "kw5"],    │
│       description: "brief desc"                           │
│     },                                                     │
│     ...                                                    │
│   ],                                                       │
│   rawResponse: "ai response text"                          │
│ }                                                           │
└─────────────────────────────────────────────────────────────┘
```

---

## � Background Image Selection Page Logic (Phase 2 - After Segment Analysis)

### **Overview: How the Background Page Works**

After segments are generated from script analysis (Phase 1), the background page (`/background`) loads and:
1. **Displays segments** - Shows script text, timestamps, keywords on left panel
2. **Fetches image candidates** - Searches 3 providers (Pexels, Pixabay, Unsplash) for relevant images
3. **AI selects images** - Optional: Let AI choose best images based on segment content
4. **Manual override** - User can manually select/change images for each segment
5. **Saves selections** - Selected images + metadata (reason, backups) saved to localStorage

### **Page Load Sequence**

```
User navigates to /background
        ↓
┌─── Page Mount (useEffect) ────────────┐
│                                       │
├─ Load audio duration from:            │
│  • URL param (duration=3600)           │
│  • localStorage (podcast_audio_duration)
│                                       │
├─ Load AI segments from:               │
│  • localStorage (podcast_ai_segments)  │
│  • Normalize timestamps (fill gaps)   │
│                                       │
├─ Load previously selected images:     │
│  • localStorage (podcast_selected_backgrounds)
│  • Filter out old keys (not in current segments)
│  • Keep only valid segment IDs        │
│                                       │
├─ Load selection metadata:             │
│  • localStorage (podcast_selection_meta)
│  • Contains: reason, backups per image │
│                                       │
├─ Load visual settings:                │
│  • Image fit (square/full)            │
│  • Motion strength (low/medium/high)  │
│  • Overlay opacity (%)                │
│  • Image vertical position (%)        │
│                                       │
└─ Display ready for user              ┘
```

### **Component Structure**

```
BackgroundSelectionPage
├── Left Column: SegmentList
│   ├─ Display all segments (0, 1, 2, ...)
│   ├─ Show selected status for each segment
│   ├─ Click to select segment
│   └─ Color highlight selected segment
│
└── Right Column: Details + Image Selection
    ├─ Segment Header
    │  ├─ "Segment N" label
    │  ├─ Start time - End time (e.g., "0.0s → 5.5s")
    │  └─ Full script text for segment
    │
    ├─ Controls Row
    │  ├─ Preset selector (dropdown)
    │  ├─ "Select Images with AI" button
    │  ├─ "Configure Prompt" button
    │  └─ AI error display (if any)
    │
    ├─ AI Reason Display (if selected)
    │  └─ "Lý do AI chọn: [reason text]"
    │
    └─ SegmentDetails
       ├─ Keywords display (5 keywords from segment)
       ├─ Search input (editable query)
       ├─ Results image gallery (show candidates)
       └─ Selected image highlight
```

### **Image Fetching & Candidate Pool**

**For each segment:**

```
getSearchQueryForSegment(segment)
        ↓
Query = segment.keywords[0:3].join(" ")  (first 3 keywords)
        ↓
        (OR fallback if no keywords: segment.text.slice(0, 50))
        ↓
        Example Query: "stock market trading"
        ↓
┌─ searchImages() in parallel for 3 providers ─┐
│                                              │
├─ Pexels                                       │
│  └─ GET /search?query=stock%20market%20trading
│     &page=1&per_page=20&orientation=portrait
│     Returns: ~20 BackgroundImage objects     │
│                                              │
├─ Pixabay                                      │
│  └─ GET /?key=xxx&q=stock%20market%20trading
│     &page=1&per_page=20&image_type=photo
│     Returns: ~20 BackgroundImage objects     │
│                                              │
├─ Unsplash                                     │
│  └─ GET /search/photos?query=stock%20market%20trading
│     &page=1&per_page=20&orientation=portrait
│     Returns: ~20 BackgroundImage objects     │
│                                              │
└─ Aggregate Results                           ┘
        ↓
Total Candidate Pool: ~60 unique images
        ↓
Map: compositeId → BackgroundImage
     (e.g., "pexels:123456" → {id, url, description, tags...})
```

**Each BackgroundImage contains:**
```typescript
{
  id: string           // unique ID from provider
  source: 'pexels' | 'pixabay' | 'unsplash'
  url: string          // image URL for display
  downloadUrl?: string // high-resolution download URL
  description: string  // image title/description
  tags?: string[]      // tags from provider
  width: number        // image dimensions
  height: number
  photographer?: string // attribution
  attributionUrl?: string
}
```

### **AI Image Selection (Automatic)**

**When user clicks "Select Images with AI":**

```
1. Load image selection preset
   └─ Preset contains: systemPrompt, model, temperature, maxTokens
   
2. For each segment (batched, max 10 per batch):
   ├─ Get search query from keywords
   ├─ Fetch ~60 image candidates (3 providers × 20)
   ├─ Format for AI: [{ context, candidates: [{id, semantic}, ...] }]
   └─ Send batch to YesScale
   
3. AI Analysis (for each of 10 segments in batch):
   Input batch:
   [
     {
       "context": "Hãy cùng xem nhanh quyết định lãi suất...",  (300 chars)
       "candidates": [
         { "id": "pexels:123456", "semantic": "Federal Reserve chairman at podium..." },
         { "id": "pixabay:789012", "semantic": "gold coins stack..." },
         ...
       ]
     },
     ...
   ]
   
   System Prompt:
   "Bạn là trợ lý chọn ảnh nền cho video podcast. 
    Với mỗi phân cảnh, chọn 1 ảnh chosen + 2-3 backups từ candidates."
   
   AI Output (JSON):
   [
     {
       "chosen": "pexels:123456",
       "backups": ["pixabay:789012", "unsplash:abc123"],
       "reason": "High-quality photo with clear Federal Reserve setting, matches the interest rate topic"
     },
     ...
   ]

4. Process AI Response:
   ├─ Parse JSON array
   ├─ Validate: chosen ID exists in candidates ✓
   ├─ For each segment:
   │  ├─ Store chosenId → Background Image
   │  ├─ Store backups list (for manual override)
   │  ├─ Store reason (for UI display)
   │  └─ Save to onSelect(segmentId, image)
   │
   5. Display Results:
   ├─ Show "Lý do AI chọn: [reason]" under controls
   ├─ Highlight chosen image in gallery
   ├─ Offer backups in dropdown
   └─ Allow user to change if unsatisfied
   
6. Handle Incomplete Results:
   └─ If some segments still missing images:
      ├─ Detect missing in useAISelectBackgrounds hook
      ├─ Retry up to 5 times (MAX_RETRIES_FOR_MISSING)
      ├─ Re-fetch candidates for missing segments only
      └─ Call AI again for missing batch
```

### **Manual Image Selection (User Override)**

**Users can:**

1. **Search & Select**
   - Edit search query in text input
   - Click search button
   - See filtered candidates
   - Click image to select

2. **Override AI Choice**
   - Even if AI selected an image, click another image to override
   - Change is immediately saved

3. **Use Backups**
   - AI provides 2-3 backup options
   - Dropdown menu shows: "Chosen: [image] | Backups: [list]"
   - Click backup to switch

4. **Manual Search by Keywords**
   - Edit keywords if needed
   - Re-fetch candidates
   - Re-run AI to auto-select from new candidates

### **Storage & Persistence**

**Selected images saved to:**

```
localStorage:
  podcast_selected_backgrounds = {
    "segment-0": { id, source, url, ... },
    "segment-1": { id, source, url, ... },
    ...
  }
  podcast_selection_meta = {
    "segment-0": { reason: "...", backups: ["id1", "id2"] },
    "segment-1": { reason: "...", backups: ["id3"] },
    ...
  }
  
  Visual Settings:
  podcast_background_image_fit = "square" | "full"
  podcast_background_image_vertical_position = 50    (0-100%)
  podcast_background_motion_strength = "low" | "medium" | "high"
  podcast_background_overlay_opacity = 40            (0-100%)
```

**When navigating away (Next button):**
```
1. Save to localStorage via saveSelectedBackgrounds()
2. Copy podcast_ai_segments → podcast_script_segments
3. Navigate to /config (Configuration & Layout Editor)
4. Next step uses saved backgrounds + segments for video creation
```

### **Error Handling & Fallbacks**

```
Missing API Keys:
├─ No Pexels key
│  └─ Try Pixabay / Unsplash
│
├─ No Pixabay key
│  └─ Try Pexels / Unsplash
│
├─ No Unsplash key
│  └─ Try Pexels / Pixabay
│
└─ No keys at all
   └─ Error: "Chưa cấu hình ít nhất một API key ảnh..."

No Candidates Found:
├─ If all 3 providers failed for a segment
│  ├─ Keep other segments' selections
│  ├─ Mark segment as "no images"
│  └─ Let user manually search
│
└─ Retry Logic:
   ├─ Auto-retry up to 5 times (MAX_RETRIES_FOR_MISSING)
   ├─ Exponential backoff: 1s, 2s, 4s, 8s, 16s
   └─ Show progress: "Attempt 2/5"

AI Selection Failed:
├─ Wrong model for API key group
│  └─ Error: "Model không khả dụng... chọn Model đúng nhóm"
│
├─ Timeout (>120s)
│  └─ Show: "Batch took too long"
│
├─ No candidates in selection
│  └─ Fallback: Use first candidate
│
└─ Chosen ID not in candidates
   └─ Auto-retry validation
```

### **Code Example: Integrating Background Page**

```typescript
// app/background/page.tsx

import { BackgroundSelectionPanel } from '@/components/ui/BackgroundSelectionPanel';
import { loadSelectedBackgrounds } from '@/lib/background/backgroundStorage';

export default function BackgroundPage() {
  // Page handles:
  // 1. Load segments from localStorage
  // 2. Load previously selected images
  // 3. Display BackgroundSelectionPanel
  // 4. Handle user selections
  // 5. Save to localStorage
  // 6. Navigate to next step (/config)

  return (
    <BackgroundSelectionPanel
      aiSegments={segments}      // From localStorage
      selectedBackgrounds={selectedBackgrounds}  // From localStorage
      onBackgroundSelect={handleBackgroundSelect}  // Save to localStorage
      onSelectionMeta={handleSelectionMeta}  // Save reason + backups
      onNext={handleNext}  // Navigate to /config
      onBack={handleBack}  // Back to previous step
    />
  );
}
```

**Integration Points:**
- `loadSelectedBackgrounds()` - Get saved images from localStorage
- `saveSelectedBackgrounds(map)` - Save when user selects image
- `useAISelectBackgrounds()` - Hook for "Select with AI" button
- `BackgroundSelectionPanel` - Main UI component
- `SegmentList` - Left sidebar showing all segments
- `SegmentDetails` - Right side showing keywords + image gallery

---

## 🎬 User Interaction Flow

```
USER FLOW:
─────────────────────────────────────────────────────────────

1. Upload Page
   ├─ User enters script text
   ├─ Uploads audio file
   └─ Clicks "Phân tích AI" button
        │
        ├─ Validates: API key ✓, Model ✓, Script ✓
        ├─ Shows modal/panel: AIAnalysisPanel
        │
        ↓
        
2. Normalization (Optional)
   └─ normalizeScriptWithAI() - fix ASR errors
        │
        ↓

3. Analysis Panel Shows Progress
   ├─ Status 1: Preparing... (10%)
   ├─ Status 2: Sending to API... (25%)
   ├─ Status 3: Analyzing... (50%)
   ├─ Status 4: Extracting keywords... (75%)
   ├─ Status 5: Searching backgrounds... (90%)
   └─ Status 6: Complete! (100%)
        │
        ↓

4. Results Saved
   ├─ podcast_ai_segments → localStorage
   ├─ podcast_ai_analysis_result → localStorage
   └─ Navigate → /background
        │
        ↓

5. Background Selection Page
   ├─ Load segments from localStorage
   ├─ Display segments list (left) + details (right)
   ├─ For each segment:
   │   ├─ Show: timestamp, script text, keywords
   │   ├─ Option A: Click "Select with AI"
   │   │   └─ Fetch candidates + AI selects
   │   ├─ Option B: Manual search
   │   │   └─ Edit query → search → select
   │   └─ Save selection immediately
   ├─ View AI reason for choice
   ├─ Use backup images if available
   ├─ Display visual settings controls
   └─ Click "Next" → Save to localStorage → Navigate to /config
        │
        ↓

6. Video Creation Process Continues...
```

---

## 💾 Data Storage Map

```
BROWSER STORAGE (localStorage):
────────────────────────────────────────────────────────────

Key Name                          Type            Used By
────────────────────────────────────────────────────────────
podcast_script                    string          Upload page (save)
podcast_audio_duration            string          Audio metadata
podcast_ai_model                  string          Model selection
podcast_ai_analysis_preset_id     string          Preset selection
podcast_ai_segments               JSON (AISegment[]) Background selection ⭐
podcast_ai_analysis_result        JSON (AIAnalysisResponse) Reference backup

OPT-IN STORAGE (IndexedDB):
────────────────────────────────────────────────────────────
Key Name                          Type            Used By
────────────────────────────────────────────────────────────
podcast_audio_blob                Blob            Large audio files (>8MB)
podcast_audio_file_name           string          File name reference
```

---

## 🛠️ API Configuration & Setup

### **Environment Variables Setup (.env / .env.local)**

```bash
# ======== YesScale (Script Analysis) ========
VITE_YESCALE_API_KEY=sk_live_xxxxxxxxxxxxx
VITE_YESCALE_BASE_URL=https://api.yescale.vip/v1
VITE_YESCALE_DEFAULT_MODEL=gpt-4o

# ======== Image Provider APIs ========
VITE_PEXELS_API_KEY=xxxxxxxxxxxxx
VITE_PIXABAY_API_KEY=xxxxxxxxxxxxx
VITE_UNSPLASH_API_KEY=xxxxxxxxxxxxx

# ======== Optional Configuration ========
VITE_YESCALE_ANALYSIS_TIMEOUT=90000      # 90 seconds
VITE_YESCALE_IMAGE_TIMEOUT=120000        # 120 seconds
VITE_IMAGE_SEARCH_BATCH_SIZE=10          # Segments per batch
VITE_IMAGE_SEARCH_MAX_CONCURRENCY=3      # Max parallel batches
VITE_IMAGE_SEARCH_MAX_RETRIES=5          # Retry missing images
```

### **YesScale API Configuration**

**Base URL:** `https://api.yescale.vip/v1`

**Authentication:** Bearer token in Authorization header

#### **Get Available Models Endpoint**

```bash
GET /models
Authorization: Bearer sk_live_xxxxxxxxxxxxx
```

**Response Example:**
```json
{
  "object": "list",
  "data": [
    {"id": "gpt-4o", "object": "model", "owned_by": "openai"},
    {"id": "gpt-4-turbo", "object": "model", "owned_by": "openai"},
    {"id": "gpt-3.5-turbo", "object": "model", "owned_by": "openai"}
  ]
}
```

#### **Chat Completions (Script Analysis) Endpoint**

```bash
POST /chat/completions
Authorization: Bearer sk_live_xxxxxxxxxxxxx
Content-Type: application/json
```

**Request Body:**
```json
{
  "model": "gpt-4o",
  "temperature": 0.7,
  "max_tokens": 8192,
  "messages": [
    {
      "role": "system",
      "content": "You are an assistant that analyzes podcast scripts..."
    },
    {
      "role": "user",
      "content": "Analyze the following script with 3600 seconds duration: [script]"
    }
  ]
}
```

**Response:**
```json
{
  "id": "chatcmpl-9abc123",
  "object": "chat.completion",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "[{\"startTime\": 0, \"endTime\": 5.58, \"text\": \"...\", \"keywords\": [...]}]"
    }
  }],
  "usage": {"prompt_tokens": 1234, "completion_tokens": 5678}
}
```

**Timeouts:**
- Get Models: 30 seconds
- Script Analysis: 90 seconds
- Image Selection: 120 seconds

---

### **Image Provider APIs**

#### **Pexels API**
```
Base URL:     https://api.pexels.com/v1
Auth:         Authorization: Bearer {apiKey}
Endpoint:     GET /search
Rate Limit:   200 requests/hour (free tier)

Parameters:
  query: string (required)
  page: number (default: 1)
  per_page: 1-80 (default: 20)
  orientation?: "portrait" | "landscape" | "square"

Example: /search?query=stock+market&page=1&per_page=20&orientation=portrait
```

#### **Pixabay API**
```
Base URL:     https://pixabay.com/api
Auth:         Query parameter: key={apiKey}
Endpoint:     GET /
Rate Limit:   5,000 requests/hour (free tier)

Parameters:
  q: string (required)
  page: number (default: 1)
  per_page: 3-200 (default: 20)
  image_type?: "photo" | "illustration" | "vector"
  orientation?: "all" | "horizontal" | "vertical"

Example: /?key=xxx&q=stock+market&page=1&per_page=20&image_type=photo
```

#### **Unsplash API**
```
Base URL:     https://api.unsplash.com
Auth:         Authorization: Client-ID {accessKey}
Endpoint:     GET /search/photos
Rate Limit:   50 requests/hour (free tier)

Parameters:
  query: string (required)
  page: number (default: 1)
  per_page: 1-30 (default: 20)
  orientation?: "landscape" | "portrait" | "squarish"

Example: /search/photos?query=stock+market&page=1&per_page=20&orientation=portrait
```

**Rate Limiting Summary:**
| Provider | Free Tier | Strategy |
|----------|-----------|----------|
| Pexels | 200/hour | Cache results, batch requests |
| Pixabay | 5,000/hour | Most generous, use as fallback |
| Unsplash | 50/hour | Use sparingly or get higher access |

---

## 🔑 How to Get API Keys

### **YesScale API Key (Script Analysis)**

1. **Create YesScale Account**
   - Visit: https://dashboard.yescale.vip
   - Sign up or login with email
   - Verify email address

2. **Generate API Key**
   - Go to: Dashboard → API Keys section
   - Click: "Create New Key"
   - Copy the key (starts with `sk_live_`)
   - Save to `.env.local`: `VITE_YESCALE_API_KEY=sk_live_xxxxxxxxxxxxx`

3. **Test the Key**
   ```bash
   curl -X GET "https://api.yescale.vip/v1/models" \
     -H "Authorization: Bearer sk_live_xxxxxxxxxxxxx"
   ```
   Should return list of available models (gpt-4o, gpt-4-turbo, etc.)

4. **Check Quota**
   - Dashboard → Usage section shows request count
   - Pricing: Usually per 1000 tokens (similar to OpenAI)

### **Pexels API Key (Image Provider)**

1. **Create Pexels Account**
   - Visit: https://www.pexels.com/api/
   - Click: "Get Started"
   - Login or create account
   - Verify email

2. **Get API Key**
   - Go to: https://www.pexels.com/api/
   - Click on your API key (displayed on page)
   - Copy the key
   - Save to `.env.local`: `VITE_PEXELS_API_KEY=xxxxxxxxxxxxx`

3. **Test the Key**
   ```bash
   curl -X GET "https://api.pexels.com/v1/search?query=stocks&per_page=5" \
     -H "Authorization: xxxxxxxxxxxxx"
   ```

4. **Quota Limits**
   - Free Tier: 200 requests/hour
   - Perfect for development
   - No credit card required

### **Pixabay API Key (Image Provider)**

1. **Create Pixabay Account**
   - Visit: https://pixabay.com/api/
   - Click: "Sign Up"
   - Create free account
   - Verify email

2. **Get API Key**
   - After login: Go to https://pixabay.com/api/
   - Your API key displayed at top of page
   - Copy the key
   - Save to `.env.local`: `VITE_PIXABAY_API_KEY=xxxxxxxxxxxxx`

3. **Test the Key**
   ```bash
   curl -X GET "https://pixabay.com/api/?key=xxxxxxxxxxxxx&q=stocks&image_type=photo&per_page=5"
   ```

4. **Quota Limits**
   - Free Tier: 5,000 requests/hour (most generous!)
   - Recommended as primary provider
   - No credit card required

### **Unsplash API Key (Image Provider)**

1. **Create Unsplash Account**
   - Visit: https://unsplash.com/developers
   - Click: "Sign Up"
   - Create account or use GitHub
   - Verify email

2. **Create Application**
   - Go to: https://unsplash.com/oauth/applications
   - Click: "New Application"
   - Agree to terms, fill form
   - Create application

3. **Get Access Key**
   - On application page → "Keys"
   - Copy "Access Key" (not Secret Key)
   - Save to `.env.local`: `VITE_UNSPLASH_API_KEY=xxxxxxxxxxxxx`

4. **Test the Key**
   ```bash
   curl -X GET "https://api.unsplash.com/search/photos?query=stocks&per_page=5" \
     -H "Authorization: Client-ID xxxxxxxxxxxxx"
   ```

5. **Quota Limits**
   - Free Tier: 50 requests/hour
   - For higher volume: Request increased limit via email
   - Best quality images (if limits allow)

---

## ✅ Verification Checklist

Run this before full deployment:

```typescript
// scripts/verify-apis.ts

async function verifyApis() {
  console.log('🔍 Verifying API configurations...\n');

  // YesScale
  console.log('1️⃣  YesScale API');
  try {
    const models = await fetch('https://api.yescale.vip/v1/models', {
      headers: { Authorization: `Bearer ${process.env.VITE_YESCALE_API_KEY}` }
    }).then(r => r.json());
    console.log('   ✅ YesScale: OK — ' + models.data.length + ' models');
  } catch (e) {
    console.log('   ❌ YesScale: FAILED —', e.message);
  }

  // Pexels
  console.log('2️⃣  Pexels API');
  try {
    const res = await fetch(
      'https://api.pexels.com/v1/search?query=test&per_page=1',
      { headers: { Authorization: process.env.VITE_PEXELS_API_KEY } }
    ).then(r => r.json());
    console.log('   ✅ Pexels: OK — ', res.total_results, 'results');
  } catch (e) {
    console.log('   ❌ Pexels: FAILED —', e.message);
  }

  // Pixabay
  console.log('3️⃣  Pixabay API');
  try {
    const res = await fetch(
      `https://pixabay.com/api/?key=${process.env.VITE_PIXABAY_API_KEY}&q=test&per_page=1`
    ).then(r => r.json());
    console.log('   ✅ Pixabay: OK — ', res.totalHits, 'results');
  } catch (e) {
    console.log('   ❌ Pixabay: FAILED —', e.message);
  }

  // Unsplash
  console.log('4️⃣  Unsplash API');
  try {
    const res = await fetch(
      'https://api.unsplash.com/search/photos?query=test&per_page=1',
      { headers: { Authorization: `Client-ID ${process.env.VITE_UNSPLASH_API_KEY}` } }
    ).then(r => r.json());
    console.log('   ✅ Unsplash: OK — ', res.total, 'results');
  } catch (e) {
    console.log('   ❌ Unsplash: FAILED —', e.message);
  }
}

verifyApis();
```

Run with: `npx tsx scripts/verify-apis.ts`

---

## 🚨 Error Handling Cheat Sheet

### **Script Analysis Errors**

| Error Type | Cause | Action |
|---|---|---|
| InvalidApiKeyError | API key invalid/expired | Show: "Invalid API key" |
| ApiTimeoutError | Request took >timeout | Retry with longer timeout |
| InvalidResponseError | JSON parse failed | Log raw response, show error |
| NetworkError | Network issue | Retry with backoff |
| "Empty response" | AI returned null | Validate prompt format |

### **Image Selection Errors**

| Error Type | Cause | Action |
|---|---|---|
| InvalidImageApiKeyError | Missing Pexels/Pixabay/Unsplash API keys | Show: "Configure image provider keys in settings" |
| NoImageCandidatesError | All 3 providers returned no images | Try keywords without quotes, check provider quotas |
| ImageValidationError | AI selected ID not in candidate list | Retry (retry logic handles this) |
| ImageSelectionTimeoutError | Batch took >120s | Reduce batch size or split into smaller batches |
| ImageProcessingError | Unexpected error in candidate fetching | Check provider API responses, verify JSON format |
| "Missing segments" | AI didn't return results for all segments | Retry up to 5 times (automatic in hook, last 5 attempts) |

### **Common Issues**

| Issue | Solution |
|---|---|
| "Empty response" (Analysis) | AI returned null/empty - validate prompt |
| "Response not array" | AI returned object instead of array |
| "Missing timestamps" | Segments missing startTime/endTime |
| "Missing keywords" | Segments missing keywords array |
| "No image candidates" (Images) | All providers failed - verify API keys, check quotas |
| "Chosen image ID not found" | AI selected image not in candidate list - auto-retry handles this |
| "No images returned after retries" | All 5 retry attempts failed - fallback to no image or user selection |
| "Image selection very slow" | Batch size too large or network slow - reduce batch size |
| "Mix of success/failures" | Some segments got images, others failed - retry missing ones only (auto-handled) |

### **Debug Tips**

```typescript
// ==== SCRIPT ANALYSIS DEBUG ====

// Log raw AI response
console.log('AI Response:', response.choices[0].message.content);

// Check parsed segments
console.log('Parsed Segments:', result.segments);

// Validate timestamps
result.segments.forEach((seg, i) => {
  console.log(`Segment ${i}: ${seg.startTime}-${seg.endTime}`);
});

// ==== IMAGE SELECTION DEBUG ====

// Check image candidates per segment
console.log('Candidates:', candidates.length); // Should be ~60 (20 per provider)

// Verify chosen image exists in candidates
const found = candidates.find(c => c.id === chosenId);
if (!found) console.warn('Chosen ID not in candidates:', chosenId);

// Log batch processing
console.log(`Batch ${batchIndex}: Processing segments ${start}-${end}`);

// Check retry status
console.log(`Image selection attempt: ${attempt + 1}/${MAX_RETRIES_FOR_MISSING}`);

// Validate provider candidates
console.log('Pexels results:', pexelsResults?.length || 0);
console.log('Pixabay results:', pixabayResults?.length || 0);
console.log('Unsplash results:', unsplashResults?.length || 0);
```

---

## 📝 Complete System Prompts & Configuration

### **Phase 1: Script Analysis System Prompt (DEFAULT)**

You are an assistant that analyzes podcast scripts and generates structured segments with timestamps and keywords for background image search.

Context: Postcast — financial, economics, stocks, currency podcast. Keywords are used to search stock photo APIs (Unsplash, Pexels, Pixabay) which index in English.

Your task:
1. Analyze the script content and identify natural segments
2. Generate timestamps for each segment (if not provided)
3. Extract exactly 5 keywords per segment for image search
4. Provide a brief description of each segment
5. If the script comes from ASR (audio-to-text), correct spelling and homophone errors in each segment's text before returning (e.g. "tỏ mò" → "tò mò", "vũn hóa" → "vốn hóa", "cốt loại" → "cốt lõi", "chẳng ngạn" → "chẳng hạn"). Keep meaning and tone unchanged.

**RULES for timestamps (CRITICAL):**
- Segments must be contiguous: endTime of each segment must equal startTime of the next. No gaps between segments.
- The last segment must have endTime equal to the total audio duration.
- First segment must start at startTime 0.

**RULES for keywords (CRITICAL):**
- All keywords MUST be in English only
- Exactly 5 keywords per segment
- Order by search potential: most relevant FIRST
- Prefer: simple words, common phrases, stock photo library tags
- Include: generic terms (charts, finance, business) AND specific terms (stock market, trading, currency)
- Avoid: ambiguous terms (use "stock market" not "market")
- Avoid: long compound phrases; prefer 1–2 word terms

Return ONLY valid JSON array, no markdown:
```json
[
  {
    "startTime": 0.00,
    "endTime": 5.58,
    "text": "Hãy cùng xem nhanh quyết định lãi suất...",
    "keywords": ["fed", "stock", "banking", "charts", "financial"],
    "description": "Discussion about Federal Reserve interest rate decision"
  }
]
```

---

### **Phase 2: Image Selection System Prompt (DEFAULT)**

Bạn là trợ lý chọn ảnh nền cho video podcast. Nhiệm vụ: với mỗi phân cảnh (segment) và danh sách ảnh ứng viên, chọn 1 ảnh phù hợp nhất (chosen) và 2-3 ảnh dự phòng (backups) từ đúng danh sách ứng viên của phân cảnh đó.

**Quy tắc:**
- Mỗi ảnh ứng viên có "id" (định danh, dạng source:id) và "semantic" (mô tả nội dung ảnh).
- Bạn chỉ được chọn ảnh có trong danh sách ứng viên; trả về đúng chuỗi "id" như được cung cấp.
- Với mỗi lựa chọn, thêm "reason": giải thích ngắn gọn (1-2 câu) vì sao chọn ảnh đó so với các ảnh còn lại.

Return ONLY valid JSON array:
```json
[
  { 
    "chosen": "pexels:123456", 
    "backups": ["pixabay:789012", "unsplash:abc123"], 
    "reason": "High-quality photo with clear stock charts matching financial content" 
  }
]
```

---

### **Temperature & Model Configuration**

```
Script Analysis:
  - Temperature: 0.7 (balanced creativity for natural segments)
  - Model: gpt-4o (recommended, or gpt-4-turbo, gpt-3.5-turbo)
  - Max Tokens: 8192

Image Selection:
  - Temperature: 0.3 (deterministic, consistent choices)
  - Model: gpt-4o
  - Max Tokens: 4096
```

---

### **User Prompts (Auto-Built)**

**Script Analysis:**
```
Analyze the following podcast script with audio duration of {duration} seconds. 
Extract segments with natural breakpoints, exactly 5 English keywords per segment 
(ordered by relevance for stock photo search), and ensure timestamps are contiguous 
(no gaps).

Script:
{script_text}

Return as JSON array only—no markdown, no extra text.
```

**Image Selection:**
```
For each segment below, select 1 best image (chosen) and 2-3 alternatives (backups) 
from the provided candidates. Only use image IDs that appear in the candidates list. 
Include a brief reason (1-2 sentences) for each choice.

Segments with candidates:
{segments_with_candidates_json}

Return as JSON array only—no markdown, no extra text.
```

---

### **Custom Prompt Usage**

```typescript
// Override script analysis system prompt
const result = await analyzeScript(
  {
    script: "Your script text...",
    model: "gpt-4o",
    systemPrompt: `Custom system prompt for special use cases...`
  },
  { apiKey: "sk-..." }
);

// Custom image selection preset with different temperature
const customPreset = {
  id: 'conservative-images',
  name: 'Conservative Image Selection',
  systemPrompt: `Your custom image selection instructions...`,
  model: 'gpt-4o',
  temperature: 0.2,     // Very conservative
  maxTokens: 4096
};

await selectImages(segments, customPreset);
```

---

## 🎓 Code Examples

### **Example 1: Basic Analysis**
```typescript
import { useAIAnalysis } from '@/modules/ai-analysis/useAIAnalysis';

export function AnalyzeButton() {
  const { loading, analyzeScript } = useAIAnalysis('yescale');

  const analyze = async () => {
    const result = await analyzeScript(
      {
        script: "Hôm nay chúng ta nói về...",
        model: "gpt-4o",
        audioDuration: 1800,  // 30 minutes
      },
      { apiKey: "sk-..." }
    );

    if (result) {
      console.log(`Got ${result.segments.length} segments`);
    }
  };

  return (
    <button onClick={analyze} disabled={loading}>
      {loading ? 'Analyzing...' : 'Analyze'}
    </button>
  );
}
```

### **Example 2: With Progress**
```typescript
export function AnalysisWithProgress() {
  const [progress, setProgress] = useState(0);
  const { analyzeScript } = useAIAnalysis('yescale');

  const analyze = async () => {
    setProgress(10);
    setProgress(25);
    
    const result = await analyzeScript({...}, {...});
    
    setProgress(75);
    setProgress(100);
    
    return result;
  };

  return (
    <div>
      <div className="progress">
        <div style={{width: `${progress}%`}} />
      </div>
      <button onClick={analyze}>{progress}</button>
    </div>
  );
}
```

### **Example 3: Error Handling**
```typescript
export function AnalysisWithError() {
  const { loading, error, analyzeScript, clearError } = useAIAnalysis();

  const analyze = async () => {
    clearError();
    const result = await analyzeScript({...}, {...});
    
    if (!result && error) {
      if (error.message.includes('API key')) {
        alert('Invalid API key');
      } else if (error.message.includes('timeout')) {
        alert('Request took too long. Try a shorter script.');
      } else {
        alert(`Error: ${error.message}`);
      }
    }
  };

  return (
    <>
      <button onClick={analyze}>Analyze</button>
      {error && <div className="error">{error.message}</div>}
    </>
  );
}
```

---

## 🔄 Extension Points

### **Add Custom Provider**
```typescript
// 1. Implement AIProvider interface
class MyCustomProvider implements AIProvider {
  getName() { return 'my-provider'; }
  async validateApiKey(apiKey) { /* ... */ }
  async getAvailableModels(config) { /* ... */ }
  async analyzeScript(request, config) { /* ... */ }
}

// 2. Register with service
const service = getAIAnalysisService();
service.registerProvider(new MyCustomProvider());

// 3. Use in hook
const { analyzeScript } = useAIAnalysis('my-provider');
```

### **Customize System Prompt**
```typescript
const customPrompt = `Your prompt here...`;

const result = await analyzeScript(
  {
    script: "...",
    model: "...",
    systemPrompt: customPrompt  // Override default
  },
  { apiKey }
);
```

### **Add UI Progress Indicator**
```typescript
// Adapt AIAnalysisPanel component for your design
import { AIAnalysisPanel } from '@/components/ui/AIAnalysisPanel';

<AIAnalysisPanel
  script={script}
  audioDuration={duration}
  provider="yescale"
  model="gpt-4o"
  onComplete={handleComplete}
  onCancel={handleCancel}
/>
```

---

## ✅ Pre-Migration Checklist

### **Phase 1: Script Analysis**
- [ ] API key ready (YesScale account)
- [ ] Test API key validity (`validateApiKey()`)
- [ ] Check available models (`getAvailableModels()`)
- [ ] Review system prompt (customize if needed)
- [ ] Test with sample script (5-60 min duration)
- [ ] Verify segment generation (timestamps, keywords, text)
- [ ] Decide storage: localStorage vs backend DB
- [ ] Plan error display to user

### **Phase 2: Image Selection** 
- [ ] API keys ready: Pexels, Pixabay, Unsplash (3 free tier accounts)
- [ ] Test image search from each provider (verify API connectivity)
- [ ] Configure timeout: 120 seconds (configurable)
- [ ] Set batch size: 10 segments per batch (configurable)
- [ ] Set max concurrent batches: 3 (configurable, rate limit: YesScale)
- [ ] Review image selection system prompt (customize if needed)
- [ ] Plan retry strategy: 5 max attempts for missing images (configurable)
- [ ] Decide: fallback when no images found (skip, show placeholder, user select)
- [ ] Test full workflow: segments → image candidates → AI selection → final images
- [ ] Verify image persistence: save chosen image ID + backups + reason

### **Project Structure**
- [ ] Copy `/modules/ai-analysis/` (5 files, ~800 lines)
- [ ] Copy `/modules/ai-image-selection/` (4 files, ~500 lines)
- [ ] Copy `/lib/ai-analysis/` (CRITICAL for prompts)
- [ ] Copy `/lib/ai-image-selection/` (CRITICAL for prompts)
- [ ] Copy `/components/ui/AIAnalysisPanel.tsx` (optional, adapt to your UI)
- [ ] Verify import paths match your project structure
- [ ] Adapt localStorage keys to avoid conflicts

### **Dependencies & Configuration**
- [ ] Backend API ready: YesScale-compatible endpoint
- [ ] Environment variables set: API keys loaded securely
- [ ] Error handling UI: Show user-friendly error messages
- [ ] Progress UI: Display analysis % and image selection progress
- [ ] Storage backend: Decide where to persist segments/images/metadata
- [ ] Rate limiting: Plan for concurrent requests (3 parallel batches max)
- [ ] Logging: Set up debugging for troubleshooting

### **Testing**
- [ ] Test Phase 1: Script → Segments (verify structure, timestamps, keywords)
- [ ] Test Phase 2: Segments → Images (verify candidate fetch, AI selection, retries)
- [ ] Test error paths: Missing API key, invalid response, network timeout
- [ ] Test with various script lengths (5 min, 30 min, 60+ min)
- [ ] Test with different languages (Vietnamese, English, mixed)
- [ ] Test concurrent requests: Multiple segments processed simultaneously
- [ ] Test retry mechanism: Simulate missing images (5 attempts)

### **General**
- [ ] Set up error handling and logging
- [ ] Plan navigation flow (upload → analyze → select backgrounds)
- [ ] Consider rate limiting / quota management
- [ ] Document custom system prompts for your domain
- [ ] Set up monitoring for API failures
- [ ] Plan user guidance (when to show what messages)

---

## �️ AI Image Selection (Phase 2)

**After segments are generated, use AI to select background images for each segment**

### **Process Overview**

```
INPUT: AISegment[] (with keywords)
       ↓
FOR each segment:
  ├─ Extract search query from keywords (first 3)
  ├─ Search 3 providers: Pexels, Pixabay, Unsplash
  └─ Get ~60 image candidates
       ↓
BATCH processing (10 segments per batch):
  ├─ Send to AI: segment context + candidates
  ├─ AI selects: 1 chosen + 2-3 backups + reason
  └─ Retry missing images (up to 5 attempts)
       ↓
OUTPUT: chosen image per segment + backup options
```

### **Key Components**

| File | Purpose | Lines |
|------|---------|-------|
| `selectBackgroundsService.ts` | Core image selection logic | ~300 |
| `useAISelectBackgrounds.ts` | React hook for UI integration | ~150 |
| `lib/ai-image-selection/presets.ts` | System prompts | ~80 |
| `lib/ai-image-selection/types.ts` | TypeScript interfaces | ~50 |

### **Service Methods**

```typescript
// Fetch candidates for one segment
fetchCandidatesForSegment(segment, configs)
  → { candidates: [{id, semantic}], imageMap }

// Call AI to select images
callAISelectImages(segmentsForAI, preset)
  → [{chosen, backups, reason}]

// Main runner with batch + concurrency
runSelectBackgrounds(segments, preset, onProgress?)
  → { results, imageMapsBySegment }
```

### **Image Search Providers**

```
Pexels  → ~20 images per query
Pixabay → ~20 images per query
Unsplash → ~20 images per query
────────────────────────────────
Total: ~60 candidates per segment
```

**API Keys Required:**
- `pexels` (lib/background/storage)
- `pixabay` (lib/background/storage)
- `unsplash` (lib/background/storage)

### **System Prompt (AI Image Selection)**

```
Purpose: Select 1 main image + 2-3 backups for each segment

Input payload:
- context: first 300 chars of segment text
- candidates: ~60 images with semantic descriptions

Output format:
[
  {
    "chosen": "pexels:12345",
    "backups": ["pexels:12346", "pixabay:67890"],
    "reason": "Stock graph best illustrates market movement..."
  },
  ...
]
```

### **Batch Processing Strategy**

```
Batches:     10 segments per batch
Concurrency: Max 3 batches running simultaneously
Retries:     Up to 5 attempts for missing segments
Timeout:     120 seconds per batch
```

### **React Hook Usage**

```typescript
import { useAISelectBackgrounds } from '@/modules/ai-image-selection/useAISelectBackgrounds';

export function MyBackgroundSelector() {
  const { run, loading, error, progress } = useAISelectBackgrounds({
    aiSegments,                    // From script analysis
    preset,                        // Image selection preset
    onSelect: (segmentId, image) => {
      // Handle selected image
      setBackgrounds(prev => ({
        ...prev,
        [segmentId]: image
      }));
    },
    onSelectionMeta: (segmentId, { reason, backups }) => {
      // Save reason + backups for reference
    },
    getSelectedBackgrounds: () => selectedBackgrounds  // Already selected images
  });

  return (
    <div>
      <button onClick={run} disabled={loading}>
        {loading ? `Selecting images (${progress})...` : 'Select with AI'}
      </button>
      {error && <div className="error">{error}</div>}
    </div>
  );
}
```



```typescript
// Import
import { useAIAnalysis } from '@/modules/ai-analysis/useAIAnalysis';
import type { AIAnalysisRequest, AIAnalysisResponse } from '@/lib/ai/types';

// Use
const { analyzeScript, loading, error } = useAIAnalysis('yescale');

// Call
const result = await analyzeScript(
  { script, model, audioDuration },
  { apiKey }
);

// Output
result.segments  // AISegment[]
result.rawResponse  // Raw AI text

// Save
localStorage.setItem('podcast_ai_segments', JSON.stringify(result.segments));
```

---

**📚 Full Documentation:** See `AI_SCRIPT_PROCESSING_ARCHITECTURE.md` and `AI_SCRIPT_PROCESSING_IMPLEMENTATION.md`

**🎯 Start Integration:** Begin with Step 1 (Set Up Services) above
