# Enhanced Performance Optimization - GPU Acceleration & Advanced Techniques

**Date**: February 13, 2026  
**Status**: Phase 2 Implementation  
**Goal**: Match CapCut smoothness with GPU acceleration

---

## 🔍 **Root Cause Analysis - Why Still Laggy?**

### **Current Bottlenecks:**

1. ❌ **No GPU Acceleration Enabled**
   - WPF không tự động enable GPU rendering
   - Cần set `RenderMode` explicitly

2. ❌ **Converter Runs on UI Thread**
   - `VideoFrameAtTimeConverter.Convert()` blocks UI
   - Even async FFmpeg doesn't help if converter synchronous

3. ❌ **No Virtualization**
   - All segments render simultaneously
   - 100 segments = 500 images loaded = lag

4. ❌ **BitmapScalingMode = LowQuality**
   - Ironically slower for scrolling
   - GPU can handle HighQuality better

5. ❌ **Frame-by-Frame Preview**
   - Not using MediaElement for smooth video playback
   - CapCut uses GPU-decoded video streams

---

## 🚀 **Phase 2 Optimizations**

### **P0 - Critical GPU Fixes**

#### 1. Enable WPF Hardware Acceleration
```csharp
// App.xaml.cs - OnStartup
protected override void OnStartup(StartupEventArgs e)
{
    // Force GPU rendering
    RenderOptions.ProcessRenderMode = RenderMode.Default; 
    // or RenderMode.SoftwareOnly for testing
    
    // Enable hardware acceleration for specific scenarios
    Timeline.DesiredFrameRateProperty.OverrideMetadata(
        typeof(Timeline),
        new FrameworkPropertyMetadata { DefaultValue = 60 });
    
    base.OnStartup(e);
}
```

#### 2. Fix BitmapScalingMode for Scrolling
```xml
<!-- WRONG: LowQuality is CPU-based, slow for scrolling -->
<Image RenderOptions.BitmapScalingMode="LowQuality"/>

<!-- RIGHT: HighQuality uses GPU when available -->
<Image RenderOptions.BitmapScalingMode="HighQuality"
       RenderOptions.EdgeMode="Aliased"/>

<!-- OR: For timeline strips, use Fant (GPU-accelerated) -->
<Image RenderOptions.BitmapScalingMode="Fant"
       RenderOptions.CachingHint="Cache"
       CacheMode="BitmapCache"/>
```

**Why?**
- `LowQuality`: Nearest-neighbor on CPU → fast decode, slow scroll
- `HighQuality`/`Fant`: Bilinear on GPU → slower decode, **smooth scroll**
- For **scrolling content**, GPU scaling >> CPU scaling

#### 3. Enable Bitmap Caching for Segments
```xml
<Border CacheMode="BitmapCache" 
        RenderOptions.CachingHint="Cache">
    <Image Source="{Binding Thumbnail}"/>
</Border>
```

Benefits:
- WPF caches rendered bitmap on GPU
- Scrolling just moves GPU texture (instant)
- No re-decode on scroll

---

### **P1 - Async Converter Pattern**

#### Problem: Converters are Synchronous
WPF converters MUST return immediately. Current code blocks:
```csharp
public object Convert(...)
{
    var thumbPath = FFmpegService.GetOrCreateVideoThumbnailPath(...); // BLOCKS!
    return LoadBitmap(thumbPath);
}
```

#### Solution: Placeholder + Background Load
```csharp
public class AsyncVideoFrameConverter : IMultiValueConverter
{
    private static readonly BitmapImage PlaceholderImage = CreatePlaceholder();
    private static readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
    
    public object Convert(object[] values, ...)
    {
        var cacheKey = GetCacheKey(...);
        
        // Return cached immediately
        if (s_frameCache.TryGet(cacheKey, out var cached))
            return cached;
        
        // Check if already loading
        if (_loadingTasks.ContainsKey(cacheKey))
            return PlaceholderImage;
        
        // Start async load
        var task = Task.Run(async () =>
        {
            try
            {
                var thumbPath = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(...);
                if (thumbPath != null)
                {
                    var bmp = LoadBitmapImage(thumbPath);
                    s_frameCache.Add(cacheKey, bmp);
                    
                    // Notify UI to refresh (need PropertyChanged trigger)
                    NotifyImageLoaded(cacheKey);
                }
            }
            finally
            {
                _loadingTasks.TryRemove(cacheKey, out _);
            }
        });
        
        _loadingTasks[cacheKey] = task;
        return PlaceholderImage; // Return immediately
    }
}
```

**Challenge**: How to notify UI when async load completes?
- Option A: Use `INotifyPropertyChanged` on segment ViewModel
- Option B: Use `Dispatcher.BeginInvoke` to trigger re-binding
- Option C: Use `ReactiveUI` or similar framework

---

### **P1 - Timeline Virtualization**

#### Current: All Segments Rendered
```xml
<ItemsControl ItemsSource="{Binding Segments}">
    <!-- Renders ALL segments, even off-screen -->
</ItemsControl>
```

#### Solution: VirtualizingPanel
```xml
<ItemsControl ItemsSource="{Binding Segments}">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel Orientation="Horizontal"
                                   VirtualizationMode="Recycling"
                                   IsVirtualizing="True"
                                   CacheLength="1,1"
                                   CacheLengthUnit="Page"/>
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
</ItemsControl>
```

**But**: Timeline is scrolled horizontally in a custom ScrollViewer
- Need to ensure `ScrollViewer.CanContentScroll="True"`
- May require custom `VirtualizingPanel` for timeline layout

---

### **P1 - GPU Video Preview (MediaElement)**

#### Replace Static Frames with Live Video
```xml
<!-- CanvasView.xaml -->
<Grid>
    <!-- Background video layer -->
    <MediaElement x:Name="VideoPlayer"
                  LoadedBehavior="Manual"
                  UnloadedBehavior="Stop"
                  ScrubbingEnabled="True"
                  Volume="0"
                  Stretch="Uniform"
                  RenderOptions.BitmapScalingMode="HighQuality">
        <!-- MediaElement uses GPU hardware decoding -->
    </MediaElement>
    
    <!-- Overlay elements (text, graphics) -->
    <Canvas x:Name="OverlayCanvas"/>
</Grid>
```

```csharp
// CanvasViewModel.cs
private void UpdateVideoPreview(Segment segment, double playheadSeconds)
{
    if (segment.BackgroundAsset?.Type == "Video")
    {
        var videoPath = segment.BackgroundAsset.FilePath;
        var timeInVideo = playheadSeconds - segment.StartTime;
        
        // Use MediaElement for smooth playback
        Application.Current.Dispatcher.Invoke(() =>
        {
            _videoPlayer.Source = new Uri(videoPath);
            _videoPlayer.Position = TimeSpan.FromSeconds(timeInVideo);
            _videoPlayer.Play();
            _videoPlayer.Pause(); // Pause at frame for scrubbing
        });
    }
}
```

**Benefits**:
- GPU hardware video decode (H.264/HEVC)
- Smooth scrubbing with `ScrubbingEnabled`
- 60fps playback capability
- Native video codec support

---

### **P2 - Advanced Techniques (Like CapCut)**

#### 1. **Double-Buffered Rendering**
```csharp
// Render to offscreen buffer, then swap
WriteableBitmap _frontBuffer;
WriteableBitmap _backBuffer;

void RenderFrame()
{
    // Render to back buffer
    _backBuffer.Lock();
    // ... draw to _backBuffer.BackBuffer ptr ...
    _backBuffer.Unlock();
    
    // Swap buffers (instant)
    (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
    OnPropertyChanged(nameof(DisplayBitmap));
}
```

#### 2. **DirectX/SharpDX Integration**
```csharp
// Use SharpDX for hardware video decode
using SharpDX.MediaFoundation;
using SharpDX.Direct3D11;

class HardwareVideoDecoder
{
    public async Task<Texture2D> DecodeFrameGPU(string videoPath, double time)
    {
        // Use Media Foundation + D3D11 for GPU decode
        // Returns GPU texture directly (no CPU roundtrip)
    }
}
```

#### 3. **Frame Queue with Predictive Loading**
```csharp
class FramePreloader
{
    private readonly Queue<(double time, BitmapSource frame)> _frameQueue = new();
    
    public void StartPredictiveLoad(double currentTime, double direction)
    {
        // Preload next 10 frames in scrub direction
        for (int i = 1; i <= 10; i++)
        {
            var nextTime = currentTime + (i * 0.1 * direction);
            _ = PreloadFrameAsync(nextTime);
        }
    }
}
```

---

## 📊 **Performance Comparison**

| Feature | Current | With GPU | CapCut-Level |
|---------|---------|----------|--------------|
| **Video decode** | CPU FFmpeg | GPU MediaElement | ✅ GPU DirectX |
| **Scaling** | CPU LowQuality | GPU HighQuality | ✅ GPU Shader |
| **Caching** | Memory only | BitmapCache (GPU) | ✅ GPU VRAM |
| **Virtualization** | None | VirtualizingPanel | ✅ Custom viewport |
| **Render mode** | Software fallback | Hardware accelerated | ✅ DirectX 11/12 |
| **Frame rate** | 10fps (quantized) | 30fps (MediaElement) | ✅ 60fps (native) |

---

## 🎯 **Implementation Priority**

### **Quick Wins (Today)**
1. ✅ Enable GPU rendering in App.xaml.cs
2. ✅ Change BitmapScalingMode → HighQuality + BitmapCache
3. ✅ Add CacheMode to segments
4. ✅ Test scroll performance

### **Medium (This Week)**
5. ⬜ Implement async converter pattern
6. ⬜ Add MediaElement for canvas preview
7. ⬜ Timeline virtualization

### **Advanced (Future)**
8. ⬜ SharpDX/DirectX integration
9. ⬜ Predictive frame loading
10. ⬜ Custom GPU shader effects

---

## 🔧 **Diagnostic Commands**

```powershell
# Check if GPU rendering enabled
Get-Process PodcastVideoEditor.Ui | Select-Object -ExpandProperty MainWindowHandle
# Use Snoop or WPF Inspector to verify RenderMode

# Monitor GPU usage
Get-Counter '\GPU Engine(*)\Utilization Percentage'
# Should see spike when scrolling timeline

# Check video codec support
ffprobe -v error -select_streams v:0 -show_entries stream=codec_name video.mp4
# H.264/HEVC should use hardware decode
```

---

## 💡 **Key Insight: Why CapCut is Smoother**

1. **GPU Pipeline**: Video decode → GPU texture → shader → display
   - No CPU↔GPU transfer
   - All operations on GPU (instant)

2. **Zero-Copy Rendering**: 
   - Video frames stay in GPU VRAM
   - Composition done in GPU shader
   - Display directly from VRAM

3. **Predictive Loading**:
   - AI predicts scrub direction
   - Preloads frames ahead of playhead
   - Always has next frame ready

4. **Custom Renderer**:
   - Not using WPF/Qt standard controls
   - DirectX/Metal/Vulkan custom renderer
   - Optimized for video editing workflow

---

**Our Current Approach**: WPF + FFmpeg (CPU) → Good for general apps  
**CapCut Approach**: DirectX + Hardware Decode (GPU) → Optimized for video

**Next Step**: Implement Quick Wins first, then gradually move toward GPU pipeline.
