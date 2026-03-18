# 🏗️ AI Script Processing - Implementation Guide

Complete code architecture and implementation details for migrating AI script processing logic to another project.

---

## 📐 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        PRESENTATION LAYER                          │
├─────────────────────────────────────────────────────────────────────┤
│  Upload Page                                                        │
│  ├─ Script Input (ScriptEditor)                                   │
│  ├─ Audio Upload                                                   │
│  ├─ Button: "Phân tích AI"                                         │
│  └─ AIAnalysisPanel (modal/panel)                                 │
│      └─ Progress indicators + status steps                         │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────────┐
│                        HOOK LAYER                                   │
├─────────────────────────────────────────────────────────────────────┤
│  useAIAnalysis()                                                    │
│  ├─ State: loading, error, models, analysisResult                 │
│  ├─ Functions: validateApiKey(), getModels(), analyzeScript()    │
│  └─ Calls: AIAnalysisService                                      │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────────┐
│                      SERVICE LAYER (SPI)                            │
├─────────────────────────────────────────────────────────────────────┤
│  AIAnalysisService (Singleton)                                     │
│  ├─ Providers Registry: Map<string, AIProvider>                   │
│  ├─ Default Provider: "yescale"                                   │
│  ├─ Methods:                                                       │
│  │  • registerProvider(provider)                                  │
│  │  • getProvider(name)                                           │
│  │  • analyzeScript(request, config, provider)                   │
│  └─ Delegates to: YesScaleProvider                                │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────────┐
│                    PROVIDER LAYER                                   │
├─────────────────────────────────────────────────────────────────────┤
│  YesScaleProvider implements AIProvider                            │
│  ├─ API: https://api.yescale.vip/v1                              │
│  ├─ Methods:                                                       │
│  │  • validateApiKey(apiKey)                                     │
│  │  • getAvailableModels(config)                                 │
│  │  • analyzeScript(request, config) ← MAIN LOGIC               │
│  ├─ Sub-methods:                                                  │
│  │  • buildUserPrompt(script, audioDuration)                    │
│  │  • parseAIResponse(response, audioDuration)                  │
│  │  • fetchChatCompletions(request, apiKey)                     │
│  │  • fetchWithRetry(url, options, maxRetries, timeout)         │
│  └─ Returns: AIAnalysisResponse                                   │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────────┐
│                      EXTERNAL API                                   │
├─────────────────────────────────────────────────────────────────────┤
│  YesScale (OpenAI-compatible API)                                  │
│  POST https://api.yescale.vip/v1/chat/completions                 │
│  ├─ Auth: Bearer <apiKey>                                         │
│  ├─ Payload:                                                       │
│  │  {                                                              │
│  │    "model": "gpt-4o",                                          │
│  │    "messages": [                                               │
│  │      { "role": "system", "content": systemPrompt },           │
│  │      { "role": "user", "content": userPrompt }                │
│  │    ],                                                           │
│  │    "temperature": 0.7,                                        │
│  │    "max_tokens": 8192                                         │
│  │  }                                                              │
│  └─ Response: JSON array of segments                             │
└─────────────────────────────────────────────────────────────────────┘

                           ↓

┌─────────────────────────────────────────────────────────────────────┐
│                    STORAGE & PERSISTENCE                            │
├─────────────────────────────────────────────────────────────────────┤
│  localStorage Keys:                                                 │
│  ├─ podcast_script                                                 │
│  ├─ podcast_ai_model                                               │
│  ├─ podcast_ai_analysis_preset_id                                 │
│  ├─ podcast_ai_segments ← Main output for next step               │
│  └─ podcast_ai_analysis_result                                     │
│                                                                     │
│  ↓ Navigation                                                       │
│  /background (Background selection page)                           │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 🔐 Class Hierarchy

```
AIProvider (Interface)
├─ getName(): string
├─ validateApiKey(apiKey): Promise<boolean>
├─ getAvailableModels(config): Promise<AIModelList>
└─ analyzeScript(request, config): Promise<AIAnalysisResponse>

↑ implements

YesScaleProvider
├─ Properties:
│  ├─ baseUrl: string = 'https://api.yescale.vip/v1'
│  ├─ defaultTimeout: number = 30000
│  └─ analyzeTimeout: number = 90000
├─ Public Methods:
│  ├─ getName()
│  ├─ getBaseUrl()
│  ├─ validateApiKey(apiKey)
│  ├─ getAvailableModels(config)
│  ├─ analyzeScript(request, config) ← KEY METHOD
│  └─ fetchChatCompletions(request, apiKey)
└─ Private Methods:
   ├─ buildUserPrompt(script, audioDuration)
   ├─ parseAIResponse(response, audioDuration)
   └─ fetchWithRetry(url, options, maxRetries, timeout)
```

---

## 💎 Core Implementation Code

### **1. AIAnalysisService (Service Pattern)**

```typescript
// modules/ai-analysis/AIAnalysisService.ts

import type {
  AIProviderConfig,
  AIModelList,
  AIAnalysisRequest,
  AIAnalysisResponse,
} from '../../lib/ai/types';
import { AIProvider } from './providers/AIProvider';
import { YesScaleProvider } from './providers/YesScaleProvider';

/**
 * AI Analysis Service
 * Uses SPI (Service Provider Interface) pattern to support multiple providers
 */
export class AIAnalysisService {
  private providers: Map<string, AIProvider> = new Map();
  private defaultProvider: string = 'yescale';

  constructor() {
    // Register default provider
    this.registerProvider(new YesScaleProvider());
  }

  registerProvider(provider: AIProvider): void {
    this.providers.set(provider.getName(), provider);
  }

  getProvider(name?: string): AIProvider {
    const providerName = name || this.defaultProvider;
    const provider = this.providers.get(providerName);
    
    if (!provider) {
      throw new Error(`Provider "${providerName}" not found`);
    }
    
    return provider;
  }

  getAvailableProviders(): string[] {
    return Array.from(this.providers.keys());
  }

  setDefaultProvider(name: string): void {
    if (!this.providers.has(name)) {
      throw new Error(`Provider "${name}" not found`);
    }
    this.defaultProvider = name;
  }

  async validateApiKey(provider: string, apiKey: string): Promise<boolean> {
    const aiProvider = this.getProvider(provider);
    return aiProvider.validateApiKey(apiKey);
  }

  async getAvailableModels(
    provider: string,
    config: AIProviderConfig
  ): Promise<AIModelList> {
    const aiProvider = this.getProvider(provider);
    return aiProvider.getAvailableModels(config);
  }

  async analyzeScript(
    request: AIAnalysisRequest,
    config: AIProviderConfig,
    providerName?: string
  ): Promise<AIAnalysisResponse> {
    const aiProvider = this.getProvider(providerName);
    return aiProvider.analyzeScript(request, config);
  }
}

// Singleton instance
let serviceInstance: AIAnalysisService | null = null;

export function getAIAnalysisService(): AIAnalysisService {
  if (!serviceInstance) {
    serviceInstance = new AIAnalysisService();
  }
  return serviceInstance;
}
```

---

### **2. AIProvider Interface (Base)**

```typescript
// modules/ai-analysis/providers/AIProvider.ts

import type {
  AIProviderConfig,
  AIModelList,
  AIAnalysisRequest,
  AIAnalysisResponse,
} from '../../../lib/ai/types';

/**
 * Base interface for AI providers
 * Implementers: YesScaleProvider, OpenAIProvider, AnthropicProvider, etc.
 */
export interface AIProvider {
  /**
   * Get provider name
   */
  getName(): string;

  /**
   * Get provider's base URL (for debugging/logging)
   */
  getBaseUrl(): string;

  /**
   * Validate API key
   */
  validateApiKey(apiKey: string): Promise<boolean>;

  /**
   * Get available models from provider
   */
  getAvailableModels(config: AIProviderConfig): Promise<AIModelList>;

  /**
   * Analyze script using provider
   */
  analyzeScript(
    request: AIAnalysisRequest,
    config: AIProviderConfig
  ): Promise<AIAnalysisResponse>;
}
```

---

### **3. YesScaleProvider Implementation (MAIN LOGIC)**

```typescript
// modules/ai-analysis/providers/YesScaleProvider.ts (condensed)

import type {
  AIProviderConfig,
  AIModelList,
  AIAnalysisRequest,
  AIAnalysisResponse,
  AIChatCompletionsRequest,
  AIChatCompletionsResponse,
  AISegment,
} from '../../../lib/ai/types';
import { AIProvider } from './AIProvider';
import { DEFAULT_SYSTEM_PROMPT } from '../../../lib/ai-analysis/presets';

export class YesScaleProvider implements AIProvider {
  private readonly baseUrl = 'https://api.yescale.vip/v1';
  private readonly defaultTimeout = 30000;      // 30 seconds
  private readonly analyzeTimeout = 90000;      // 90 seconds

  getName(): string {
    return 'yescale';
  }

  getBaseUrl(): string {
    return this.baseUrl;
  }

  async validateApiKey(apiKey: string): Promise<boolean> {
    try {
      const response = await fetch(`${this.baseUrl}/models`, {
        method: 'GET',
        headers: {
          'Authorization': `Bearer ${apiKey}`,
          'Content-Type': 'application/json',
        },
        signal: AbortSignal.timeout(this.defaultTimeout),
      });
      return response.ok;
    } catch (error) {
      return false;
    }
  }

  async getAvailableModels(config: AIProviderConfig): Promise<AIModelList> {
    const apiKey = config.apiKey;
    if (!apiKey) throw new Error('API key is required');

    const response = await this.fetchWithRetry<AIModelList>(
      `${this.baseUrl}/models`,
      {
        method: 'GET',
        headers: {
          'Authorization': `Bearer ${apiKey}`,
          'Content-Type': 'application/json',
        },
      }
    );

    return {
      object: 'list',
      data: response.data || [],
    };
  }

  /**
   * ⭐ MAIN METHOD - Analyze script using YesScale API
   */
  async analyzeScript(
    request: AIAnalysisRequest,
    config: AIProviderConfig
  ): Promise<AIAnalysisResponse> {
    const apiKey = config.apiKey;
    if (!apiKey) throw new Error('API key is required');

    // 1. Build prompts
    const systemPrompt = request.systemPrompt ?? DEFAULT_SYSTEM_PROMPT;
    const userPrompt = this.buildUserPrompt(request.script, request.audioDuration);

    // 2. Build chat request
    const chatRequest: AIChatCompletionsRequest = {
      model: request.model,
      messages: [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: userPrompt },
      ],
      temperature: request.temperature ?? 0.7,
      max_tokens: request.maxTokens ?? 8192,
      stream: false,
    };

    try {
      // 3. Make API call
      const response = await this.fetchChatCompletions(chatRequest, apiKey);

      // 4. Parse response
      const segments = this.parseAIResponse(response, request.audioDuration);

      return {
        segments,
        rawResponse: response.choices[0]?.message?.content,
      };
    } catch (error) {
      throw new Error(`Failed to analyze script: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  }

  /**
   * Build user prompt for script analysis
   */
  private buildUserPrompt(script: string, audioDuration?: number): string {
    let prompt = `Analyze the following podcast script and generate segments with timestamps and keywords:\n\n`;
    prompt += `Script:\n${script}\n\n`;

    if (audioDuration) {
      prompt += `Audio duration: ${audioDuration} seconds.\n`;
      prompt += `Segments must be contiguous: endTime of each = startTime of next. First at 0, last at ${audioDuration}.\n\n`;
    }

    prompt += `Generate segments covering entire script. Estimate timestamps if not provided.`;
    return prompt;
  }

  /**
   * Make API call with retry logic
   */
  private async fetchChatCompletions(
    request: AIChatCompletionsRequest,
    apiKey: string
  ): Promise<AIChatCompletionsResponse> {
    return this.fetchWithRetry<AIChatCompletionsResponse>(
      `${this.baseUrl}/chat/completions`,
      {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${apiKey}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      },
      1,                      // Reduce retries for analyze
      this.analyzeTimeout     // Longer timeout
    );
  }

  /**
   * Fetch with retry logic (exponential backoff)
   */
  private async fetchWithRetry<T>(
    url: string,
    options: RequestInit,
    maxRetries = 3,
    timeout?: number
  ): Promise<T> {
    let lastError: Error | null = null;
    const requestTimeout = timeout || this.defaultTimeout;

    for (let attempt = 0; attempt < maxRetries; attempt++) {
      try {
        console.log(`[YesScale] Attempt ${attempt + 1}/${maxRetries}`);
        
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), requestTimeout);

        const response = await fetch(url, {
          ...options,
          signal: controller.signal,
        });
        clearTimeout(timeoutId);

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        return await response.json();
      } catch (error) {
        lastError = error instanceof Error ? error : new Error(String(error));

        if (attempt < maxRetries - 1) {
          const delay = Math.pow(2, attempt) * 1000; // exponential backoff: 1s, 2s, 4s
          console.log(`[YesScale] Retrying in ${delay}ms...`);
          await new Promise((resolve) => setTimeout(resolve, delay));
        }
      }
    }

    throw lastError || new Error(`Request failed after ${maxRetries} attempts`);
  }

  /**
   * Parse AI response JSON into segments
   */
  private parseAIResponse(
    response: AIChatCompletionsResponse,
    audioDuration?: number
  ): AISegment[] {
    const content = response.choices[0]?.message?.content;
    if (!content) throw new Error('Empty response from AI');

    try {
      // Remove markdown code blocks if present
      let jsonText = content.trim();
      if (jsonText.startsWith('```json')) {
        jsonText = jsonText.replace(/^```json\s*/, '').replace(/\s*```$/, '');
      } else if (jsonText.startsWith('```')) {
        jsonText = jsonText.replace(/^```\s*/, '').replace(/\s*```$/, '');
      }

      const segments: AISegment[] = JSON.parse(jsonText);

      if (!Array.isArray(segments)) {
        throw new Error('Response is not an array');
      }

      // Validate and normalize
      return segments.map((seg, index) => {
        if (typeof seg.startTime !== 'number' || typeof seg.endTime !== 'number') {
          throw new Error(`Segment ${index} missing timestamps`);
        }
        if (!Array.isArray(seg.keywords) || seg.keywords.length === 0) {
          throw new Error(`Segment ${index} missing keywords`);
        }

        let text = typeof seg.text === 'string' ? seg.text.trim() : '';
        if (text.length === 0) {
          console.warn(`Segment ${index} has empty text; using fallback.`);
          text = seg.description?.trim() || '(no text)';
        }

        return {
          startTime: seg.startTime,
          endTime: seg.endTime,
          text,
          keywords: seg.keywords.map((k: string) => k.trim()).filter(Boolean),
          description: seg.description?.trim(),
        };
      });
    } catch (error) {
      throw new Error(`Failed to parse AI response: ${error instanceof Error ? error.message : 'Unknown'}`);
    }
  }
}
```

---

### **4. useAIAnalysis Hook**

```typescript
// modules/ai-analysis/useAIAnalysis.ts (condensed)

import { useState, useCallback } from 'react';
import type {
  AIProviderConfig,
  AIModelList,
  AIAnalysisRequest,
  AIAnalysisResponse,
} from '../../lib/ai/types';
import { getAIAnalysisService } from './AIAnalysisService';

export interface AIAnalysisState {
  loading: boolean;
  error: Error | null;
  models: AIModelList | null;
  analysisResult: AIAnalysisResponse | null;
}

export function useAIAnalysis(provider: string = 'yescale') {
  const [state, setState] = useState<AIAnalysisState>({
    loading: false,
    error: null,
    models: null,
    analysisResult: null,
  });

  const service = getAIAnalysisService();

  const analyzeScript = useCallback(
    async (
      request: AIAnalysisRequest,
      config: AIProviderConfig
    ): Promise<AIAnalysisResponse | null> => {
      setState((prev) => ({ ...prev, loading: true, error: null }));

      try {
        const result = await service.analyzeScript(request, config, provider);
        setState((prev) => ({ ...prev, loading: false, analysisResult: result }));
        return result;
      } catch (error) {
        const err = error instanceof Error ? error : new Error('Unknown error');
        setState((prev) => ({ ...prev, loading: false, error: err }));
        return null;
      }
    },
    [provider, service]
  );

  const validateApiKey = useCallback(
    async (apiKey: string): Promise<boolean> => {
      setState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const isValid = await service.validateApiKey(provider, apiKey);
        setState((prev) => ({ ...prev, loading: false }));
        return isValid;
      } catch (error) {
        const err = error instanceof Error ? error : new Error('Unknown error');
        setState((prev) => ({ ...prev, loading: false, error: err }));
        return false;
      }
    },
    [provider, service]
  );

  const getModels = useCallback(
    async (config: AIProviderConfig): Promise<AIModelList | null> => {
      setState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const models = await service.getAvailableModels(provider, config);
        setState((prev) => ({ ...prev, loading: false, models }));
        return models;
      } catch (error) {
        const err = error instanceof Error ? error : new Error('Unknown error');
        setState((prev) => ({ ...prev, loading: false, error: err }));
        return null;
      }
    },
    [provider, service]
  );

  const clearError = useCallback(() => {
    setState((prev) => ({ ...prev, error: null }));
  }, []);

  return {
    ...state,
    analyzeScript,
    validateApiKey,
    getModels,
    clearError,
  };
}
```

---

### **5. Types Definition**

```typescript
// lib/ai/types.ts (condensed)

export interface AIProviderConfig {
  apiKey: string;
  baseUrl?: string;
}

export interface AIModel {
  id: string;
  object: string;
  created: number;
  owned_by: string;
}

export interface AIModelList {
  object: string;
  data: AIModel[];
}

export type AIMessageRole = 'system' | 'user' | 'assistant';

export interface AIMessage {
  role: AIMessageRole;
  content: string;
}

export interface AIAnalysisRequest {
  script: string;
  audioDuration?: number;
  model: string;
  temperature?: number;
  maxTokens?: number;
  systemPrompt?: string;
}

export interface AISegment {
  startTime: number;
  endTime: number;
  text: string;
  keywords: string[];
  description?: string;
  suggestedBackgrounds?: string[];
}

export interface AIAnalysisResponse {
  segments: AISegment[];
  rawResponse?: string;
}

export interface AIChatCompletionsRequest {
  model: string;
  messages: AIMessage[];
  temperature?: number;
  max_tokens?: number;
  stream?: boolean;
}

export interface AIChatCompletionsResponse {
  id: string;
  object: string;
  created: number;
  choices: Array<{
    index: number;
    message: AIMessage;
    finish_reason: string;
  }>;
  usage?: {
    prompt_tokens: number;
    completion_tokens: number;
    total_tokens: number;
  };
}

// Error types
export class AIError extends Error {
  constructor(message: string, public code?: string) {
    super(message);
    this.name = 'AIError';
  }
}

export class InvalidApiKeyError extends AIError {}
export class ApiTimeoutError extends AIError {}
export class InvalidResponseError extends AIError {}
export class NetworkError extends AIError {}
```

---

### **6. System Prompt (Preset)**

```typescript
// lib/ai-analysis/presets.ts (condensed)

export const DEFAULT_SYSTEM_PROMPT = `You are an assistant that analyzes podcast scripts and generates structured segments with timestamps and keywords for background image search.

Context: Financial podcast. Keywords for stock photo APIs (English only).

Your task:
1. Analyze script content and identify segments
2. Generate timestamps for each segment
3. Extract exactly 5 keywords per segment for image search
4. Provide brief description

RULES for timestamps (CRITICAL):
- Segments must be contiguous: endTime of each = startTime of next. No gaps.
- Last segment endTime = total audio duration
- First segment startTime = 0

RULES for keywords (CRITICAL):
- All keywords MUST be in English only
- Exactly 5 keywords per segment
- Order by search potential
- Avoid ambiguous terms

Return response as JSON array (NO markdown, NO extra text):
[
  {
    "startTime": 0.00,
    "endTime": 5.58,
    "text": "segment content",
    "keywords": ["kw1", "kw2", "kw3", "kw4", "kw5"],
    "description": "brief description"
  },
  ...
]`;
```

---

### **7. Usage in Component**

```typescript
// Example: Using in AI Analysis Panel

import { useAIAnalysis } from '@/modules/ai-analysis/useAIAnalysis';
import type { AIAnalysisRequest, AIAnalysisResponse } from '@/lib/ai/types';

export function MyAIAnalysisComponent() {
  const { loading, error, analyzeScript } = useAIAnalysis('yescale');

  const handleAnalyze = async (script: string, model: string, apiKey: string) => {
    const request: AIAnalysisRequest = {
      script,
      model,
      audioDuration: 3600, // 1 hour
      temperature: 0.7,
      maxTokens: 8192,
    };

    const result = await analyzeScript(request, { apiKey });

    if (result) {
      console.log('Segments:', result.segments);
      // Use result.segments for next step
      localStorage.setItem('podcast_ai_segments', JSON.stringify(result.segments));
    } else {
      console.error('Analysis failed:', error?.message);
    }
  };

  return (
    <div>
      <button onClick={() => handleAnalyze(...)}>Analyze</button>
      {loading && <p>Analyzing...</p>}
      {error && <p>Error: {error.message}</p>}
    </div>
  );
}
```

---

## �️ AI Image Selection - Complete Logic

**After receiving segments from AI script analysis, the next step is to use AI to select background images for each segment.**

### **Overview**

```
INPUT: AISegment[] (from script analysis)
       ├─ Each segment has: startTime, endTime, text, keywords
       │
       ↓
STEP 1: Extract search query for each segment
       ├─ Use first 3 keywords from segment.keywords
       ├─ Or fallback: segment.text.slice(0, 50)
       │
       ↓
STEP 2: Fetch image candidates from providers (parallel)
       ├─ Pexels (~20 images)
       ├─ Pixabay (~20 images)
       └─ Unsplash (~20 images)
           = Total ~60 candidates per segment
       │
       ↓
STEP 3: Batch processing with AI (max 10 segments per batch)
       ├─ Build payload: segments + candidates
       ├─ Call YesScale AI API
       └─ AI returns: chosen image + 2-3 backups + reason
       │
       ↓
STEP 4: Process results in parallel (with concurrency limit)
       ├─ Concurrency: max 3 batches running simultaneously
       ├─ Retry missing segments (up to 5 attempts)
       │
       ↓
OUTPUT: AIImageSelectionResultItem[]
        ├─ segmentIndex, chosenId, backups[], reason
        └─ Map candidate ID back to BackgroundImage object
```

### **Service Architecture**

```
selectBackgroundsService.ts (Main logic)
├─ fetchCandidatesForSegment(segment, configs)
│  ├─ Extract search query: getSearchQueryForSegment()
│  ├─ Fetch from Pexels/Pixabay/Unsplash (parallel)
│  └─ Return: { candidates, imageMap }
│
├─ callAISelectImages(segmentsForAI, preset)
│  ├─ Build system prompt (from preset)
│  ├─ Build user prompt with segment context + candidates
│  ├─ Call YesScale API (120s timeout)
│  └─ Return: { chosen, backups[], reason }[]
│
└─ runSelectBackgrounds(segments, preset, onProgress)
   ├─ Batch segments (10 per batch)
   ├─ For each batch:
   │  ├─ Fetch all candidates in parallel
   │  ├─ Call AI
   │  └─ Report progress
   ├─ Run batches with concurrency limit (max 3)
   └─ Return: { results[], imageMapsBySegment }
```

### **Data Flow: Segments to Image Selection**

```typescript
// 1. INPUT: Segment with keywords
const segment: AISegment = {
  startTime: 5.58,
  endTime: 12.00,
  text: "Tiếp theo là diễn biến thị trường chứng khoán trong tuần.",
  keywords: ["stock market", "trading", "finance", "charts", "business"],
  description: "Stock market weekly recap"
};

// 2. Extract search query
const query = segment.keywords.slice(0, 3).join(' '); // "stock market trading"

// 3. Search image candidates (from 3 providers)
const candidates = [
  { id: "pexels:12345", semantic: "stock market graph trending up" },
  { id: "pexels:12346", semantic: "businessman analyzing charts" },
  { id: "pixabay:67890", semantic: "financial data visualization" },
  { id: "unsplash:11111", semantic: "trading desk with multiple screens" },
  // ... total ~60 candidates
];

// 4. AI receives payload
const payload = {
  context: "Tiếp theo là diễn biến thị trường chứng khoán trong tuần.",  // 300 chars max
  candidates: [
    { id: "pexels:12345", semantic: "stock market graph trending up" },
    { id: "pexels:12346", semantic: "businessman analyzing charts" },
    // ... all ~60 candidates
  ]
};

// 5. AI returns
const aiResult = {
  chosen: "pexels:12345",
  backups: ["pexels:12346", "pixabay:67890"],
  reason: "Stock market graph with uptrend best illustrates the topic of market performance and trading activity."
};

// 6. Map ID back to BackgroundImage object
const chosenImage: BackgroundImage = imageMap.get("pexels:12345");
// = { id: "12345", source: "pexels", url: "...", description: "...", tags: [...] }
```

### **8. AI Image Selection Service**

```typescript
// modules/ai-image-selection/selectBackgroundsService.ts (condensed)

import type { AISegment } from '@/lib/ai/types';
import type { BackgroundImage } from '@/lib/background/types';
import type { AIImageSelectionPreset, AIImageSelectionResultItem } from '@/lib/ai-image-selection/types';

/**
 * Extract search query from segment (keywords or text)
 */
function getSearchQueryForSegment(segment: AISegment): string {
  if (segment.keywords?.length) {
    return segment.keywords.slice(0, 3).join(' ');  // Use first 3 keywords
  }
  return segment.text.slice(0, 50).trim() || 'nature';
}

/**
 * Fetch ~60 image candidates (20 from each provider)
 */
export async function fetchCandidatesForSegment(
  segment: AISegment,
  configs: Record<string, { apiKey: string }>
): Promise<{
  candidates: Array<{ id: string; semantic: string }>;
  imageMap: Map<string, BackgroundImage>;
}> {
  const query = getSearchQueryForSegment(segment);
  const service = getBackgroundManagerService();
  const candidates: Array<{ id: string; semantic: string }> = [];
  const imageMap = new Map<string, BackgroundImage>();

  const req = {
    query,
    page: 1,
    perPage: 20,        // Per provider
    orientation: 'portrait',
  };

  // Fetch from all 3 providers in parallel
  const providers = ['pexels', 'pixabay', 'unsplash'] as const;
  const providerResults = await Promise.allSettled(
    providers
      .filter((name) => !!configs[name]?.apiKey)
      .map(async (name) => {
        const res = await service.searchImages(req, { apiKey: configs[name].apiKey }, name);
        return res.images.slice(0, 20);
      })
  );

  // Collect candidates
  for (const result of providerResults) {
    if (result.status === 'fulfilled') {
      for (const img of result.value) {
        const cid = `${img.source}:${img.id}`;  // Composite ID
        const semantic = [img.description, ...(img.tags || [])].filter(Boolean).join(' ');
        candidates.push({ id: cid, semantic });
        imageMap.set(cid, img);
      }
    }
  }

  return { candidates, imageMap };
}

/**
 * Call AI to select images for batch of segments
 */
export async function callAISelectImages(
  segmentsForAI: Array<{ context: string; candidates: Array<{ id: string; semantic: string }> }>,
  preset: AIImageSelectionPreset
): Promise<Array<{ chosen: string; backups: string[]; reason: string }>> {
  const apiKey = getAiApiKey('yescale');
  if (!apiKey) throw new Error('API key YesScale chưa cấu hình.');

  // Use model from Upload page (if selected)
  const savedModel = localStorage.getItem('podcast_ai_model') || preset.model || 'gpt-4o-mini';
  const model = savedModel.trim() || 'gpt-4o-mini';
  const temperature = preset.temperature ?? 0.3;  // Lower temperature for consistent choices
  const maxTokens = preset.maxTokens ?? 2048;

  const body = {
    model,
    messages: [
      { role: 'system', content: preset.systemPrompt },
      {
        role: 'user',
        content: `Cho ${segmentsForAI.length} phân cảnh sau. Với mỗi phân cảnh, chọn 1 ảnh phù hợp nhất và 2-3 ảnh dự phòng từ danh sách candidates, kèm lý do (reason). Trả về đúng mảng JSON.\n\n${JSON.stringify({ segments: segmentsForAI })}`,
      },
    ],
    temperature,
    max_tokens: maxTokens,
  };

  const res = await fetch('https://api.yescale.vip/v1/chat/completions', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${apiKey}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(120000),  // 2 minutes timeout
  });

  if (!res.ok) {
    const errText = await res.text();
    throw new Error(`AI API ${res.status}: ${errText}`);
  }

  const data = (await res.json()) as { choices?: Array<{ message?: { content?: string } }> };
  const content = data.choices?.[0]?.message?.content?.trim();
  if (!content) throw new Error('AI không trả về nội dung.');

  // Extract JSON (might be wrapped in markdown code blocks)
  let jsonStr = content;
  const codeMatch = content.match(/```(?:json)?\s*([\s\S]*?)```/);
  if (codeMatch) jsonStr = codeMatch[1].trim();

  const parsed = JSON.parse(jsonStr) as Array<{ chosen: string; backups?: string[]; reason?: string }>;
  return parsed.map((p) => ({
    chosen: p.chosen ?? '',
    backups: Array.isArray(p.backups) ? p.backups : [],
    reason: typeof p.reason === 'string' ? p.reason : '',
  }));
}

/**
 * Main function: Run image selection for all segments with batch processing
 */
export async function runSelectBackgrounds(
  segments: AISegment[],
  preset: AIImageSelectionPreset,
  onProgress?: (message: string) => void
): Promise<{
  results: AIImageSelectionResultItem[];
  imageMapsBySegment: Map<number, Map<string, BackgroundImage>>;
}> {
  const configs: Record<string, { apiKey: string }> = {};
  
  // Get API keys for image providers
  for (const name of ['pexels', 'pixabay', 'unsplash']) {
    const key = getBackgroundApiKey(name);
    if (key) configs[name] = { apiKey: key };
  }
  if (Object.keys(configs).length === 0) {
    throw new Error('Chưa cấu hình ít nhất một API key ảnh (Pexels/Pixabay/Unsplash).');
  }

  const imageMapsBySegment = new Map<number, Map<string, BackgroundImage>>();
  const SEGMENTS_PER_BATCH = 10;
  const BATCH_CONCURRENCY = 3;  // Max 3 batches in parallel

  // Build batches
  const batches: Array<{ start: number; segs: AISegment[] }> = [];
  for (let start = 0; start < segments.length; start += SEGMENTS_PER_BATCH) {
    batches.push({ start, segs: segments.slice(start, start + SEGMENTS_PER_BATCH) });
  }

  let completedBatches = 0;
  onProgress?.(`Processing ${segments.length} segments in ${batches.length} batches...`);

  // Each batch: fetch candidates + call AI
  const batchTasks = batches.map(({ start, segs }) => async () => {
    // Step 1: Fetch candidates for all segments in batch (parallel)
    const candidateResults = await Promise.all(
      segs.map((seg) => fetchCandidatesForSegment(seg, configs))
    );

    // Step 2: Build payload for AI
    const segmentsForAI: Array<{ context: string; candidates: Array<{ id: string; semantic: string }> }> = [];
    for (let i = 0; i < segs.length; i++) {
      const { candidates, imageMap } = candidateResults[i];
      segmentsForAI.push({
        context: segs[i].text.slice(0, 300),  // First 300 chars of segment text
        candidates,  // ~60 candidates
      });
      imageMapsBySegment.set(start + i, imageMap);
    }

    // Step 3: Call AI to select images
    const aiResults = await callAISelectImages(segmentsForAI, preset);

    // Update progress
    completedBatches++;
    const done = Math.min(start + segs.length, segments.length);
    onProgress?.(`Processed ${done}/${segments.length} segments (${completedBatches}/${batches.length} batches)...`);

    return { start, segs, aiResults };
  });

  // Run batches with concurrency limit
  async function runWithConcurrency<T>(tasks: Array<() => Promise<T>>, limit: number): Promise<T[]> {
    const results: T[] = new Array(tasks.length);
    let index = 0;
    async function worker() {
      while (index < tasks.length) {
        const i = index++;
        results[i] = await tasks[i]();
      }
    }
    await Promise.all(Array.from({ length: Math.min(limit, tasks.length) }, worker));
    return results;
  }

  const batchResults = await runWithConcurrency(batchTasks, BATCH_CONCURRENCY);

  // Collect results in segment order
  const results: AIImageSelectionResultItem[] = [];
  for (const { start, segs, aiResults } of batchResults) {
    for (let i = 0; i < segs.length; i++) {
      const r = aiResults[i];
      results.push({
        segmentIndex: start + i,
        segmentId: `segment-${start + i}`,
        chosenId: r?.chosen ?? '',
        backups: r?.backups ?? [],
        reason: r?.reason ?? '',
      });
    }
  }

  return { results, imageMapsBySegment };
}
```

### **9. useAISelectBackgrounds Hook**

```typescript
// modules/ai-image-selection/useAISelectBackgrounds.ts

import { useState, useCallback } from 'react';
import type { AISegment } from '@/lib/ai/types';
import type { BackgroundImage } from '@/lib/background/types';
import type { AIImageSelectionPreset } from '@/lib/ai-image-selection/types';
import { runSelectBackgrounds } from './selectBackgroundsService';

export function useAISelectBackgrounds({
  aiSegments,
  preset,
  onSelect,
  onSelectionMeta,  // { reason, backups }
  getSelectedBackgrounds,  // Map of already selected images
}: {
  aiSegments: AISegment[];
  preset: AIImageSelectionPreset | null;
  onSelect: (segmentId: string, image: BackgroundImage) => void;
  onSelectionMeta?: (segmentId: string, meta: { reason: string; backups: string[] }) => void;
  getSelectedBackgrounds?: () => Map<string, BackgroundImage>;
}) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<string | null>(null);

  const run = useCallback(async () => {
    if (!preset || aiSegments.length === 0) {
      setError('Missing preset or segments');
      return;
    }

    setLoading(true);
    setError(null);
    setProgress('Starting...');

    try {
      const { results, imageMapsBySegment } = await runSelectBackgrounds(
        aiSegments,
        preset,
        (msg) => setProgress(msg)
      );

      // Map results back and call callbacks
      for (const r of results) {
        const segmentId = `segment-${r.segmentIndex}`;
        const imageMap = imageMapsBySegment.get(r.segmentIndex);
        const img = imageMap?.get(r.chosenId);
        
        if (img) {
          onSelect(segmentId, img);
          onSelectionMeta?.(segmentId, { reason: r.reason, backups: r.backups });
        }
      }

      setProgress(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Image selection failed');
      setProgress(null);
    } finally {
      setLoading(false);
    }
  }, [aiSegments, preset, onSelect, onSelectionMeta]);

  return { run, loading, error, progress };
}
```

### **10. System Prompt for Image Selection**

```typescript
// lib/ai-image-selection/presets.ts

export const DEFAULT_SYSTEM_PROMPT = `Bạn là trợ lý chọn ảnh nền cho video podcast. Nhiệm vụ: với mỗi phân cảnh (segment) và danh sách ảnh ứng viên, chọn 1 ảnh phù hợp nhất (chosen) và 2-3 ảnh dự phòng (backups) từ đúng danh sách ứng viên của phân cảnh đó.

Quy tắc:
- Mỗi ảnh ứng viên có "id" (định danh) và "semantic" (mô tả nội dung ảnh).
- Bạn chỉ được chọn ảnh có trong danh sách ứng viên; trả về đúng chuỗi "id".
- Với mỗi lựa chọn, thêm "reason": giải thích ngắn gọn (1-2 câu) vì sao chọn ảnh đó.

Output: JSON array, mỗi phần tử = một phân cảnh:
[
  {
    "chosen": "pexels:12345",
    "backups": ["pexels:12346", "pixabay:67890"],
    "reason": "Stock market graph illustrates the economic trend topic."
  },
  ...
]

Chỉ trả mảng JSON, không markdown, không thêm text ngoài JSON.`;
```

### **Image Selection Data Types**

```typescript
// lib/ai-image-selection/types.ts

export interface AIImageSelectionPreset {
  id: string;
  name: string;
  systemPrompt: string;
  model?: string;                  // e.g., 'gpt-4o-mini'
  temperature?: number;            // Default: 0.3 (lower = more consistent)
  maxTokens?: number;              // Default: 2048
  userPromptTemplate?: string;
  createdAt: number;
  updatedAt: number;
}

export interface AIImageSelectionResultItem {
  segmentIndex: number;
  segmentId: string;
  chosenId: string;                // Composite ID: "source:id"
  backups: string[];
  reason: string;
}

export interface AISelectedSegmentMeta {
  reason: string;                  // Why this image was chosen
  backups: string[];               // Backup image IDs
}
```

---

## 📦 File Structure for Migration

```
new-project/
├── lib/
│   ├── ai/
│   │   ├── types.ts                    ← Copy from lib/ai/types.ts
│   │   └── storage.ts                  ← Copy (or adapt)
│   ├── ai-analysis/
│   │   ├── types.ts                    ← Copy
│   │   └── presets.ts                  ← Copy (system prompts)
│   ├── ai-image-selection/             ← NEW
│   │   ├── types.ts                    ← Copy
│   │   └── presets.ts                  ← Copy (image selection prompts)
│   └── background/
│       ├── types.ts                    ← BackgroundImage interface
│       └── storage.ts                  ← API key storage
├── modules/
│   ├── ai-analysis/
│   │   ├── AIAnalysisService.ts        ← Copy (CRITICAL)
│   │   ├── useAIAnalysis.ts            ← Copy (CRITICAL)
│   │   └── providers/
│   │       ├── AIProvider.ts
│   │       └── YesScaleProvider.ts     ← Copy (CRITICAL)
│   ├── ai-image-selection/             ← NEW
│   │   ├── selectBackgroundsService.ts ← Copy (CRITICAL)
│   │   ├── useAISelectBackgrounds.ts   ← Copy (CRITICAL)
│   │   └── index.ts
│   └── background-manager/
│       └── BackgroundManagerService.ts ← Image search from Pexels/Pixabay
└── components/
    └── ui/
        ├── AIAnalysisPanel.tsx
        └── AISelectBackgroundsPanel.tsx ← New: Image selection UI
```

---

## 🔧 Integration Checklist

### **For Script Analysis (Phase 1)**
- [ ] Copy core service files (AIAnalysisService, YesScaleProvider)
- [ ] Copy types definitions (lib/ai/types.ts, lib/ai-analysis/types.ts)
- [ ] Copy hook (useAIAnalysis)
- [ ] Copy UI components (AIAnalysisPanel)
- [ ] Configure API key storage (localStorage or backend)
- [ ] Test API connectivity (validateApiKey)
- [ ] Test model fetching (getModels)
- [ ] Test script analysis end-to-end
- [ ] Customize system prompt if needed

### **For Image Selection (Phase 2)**
- [ ] Copy image selection service (selectBackgroundsService.ts)
- [ ] Copy image selection hook (useAISelectBackgrounds.ts)
- [ ] Copy image selection types & presets
- [ ] Configure background provider API keys (Pexels/Pixabay/Unsplash)
- [ ] Implement BackgroundManagerService for image search
- [ ] Test candidate fetching from providers
- [ ] Test AI image selection end-to-end
- [ ] Handle retry logic for missing images (up to 5 attempts)
- [ ] Customize image selection system prompt if needed

### **General**
- [ ] Adapt localStorage keys if different naming convention
- [ ] Add error handling UI for your project
- [ ] Update routing based on new project structure
- [ ] Set up logging for debugging

---

*Architecture: Service Provider Interface (SPI) pattern for extensibility*  
*API: OpenAI-compatible via YesScale*  
*State Management: React hooks + localStorage*  
*Error Handling: Retry logic with exponential backoff*
