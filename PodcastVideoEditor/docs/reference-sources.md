# Reference Sources - Open Source Projects to Study

**Project:** Podcast Video Editor  
**Purpose:** Timeline & Component reference sources from GitHub  
**Created:** 2026-02-06

---

## 🎯 Timeline Control - Tiêu Biểu (Open Source Projects)

### 1. **Gemini (Gemini.Modules.Timeline)** ⭐ KHUYÊN DÙNG NHẤT

**URL:** [Gemini WPF Framework](https://github.com/tgjones/gemini)

**Mô tả:** Framework WPF giống Visual Studio (Docking, Shell) với một module Timeline chuyên nghiệp.

**Giá trị tham khảo:**
- ✅ Kiến trúc MVVM cho Timeline (Tách Track, Clip, View)
- ✅ Xử lý Zoom & Scroll cực chuẩn
- ✅ Vẽ thước đo thời gian (Time ruler)
- ✅ Virtualization cho hiệu suất cao

**Cách tìm:**
```
GitHub search: "Gemini.Modules.Timeline"
File cần xem: /src/Gemini/Modules/Timeline/
```

**Copy từ đây:**
- Công thức tính: PixelsPerSecond, TimeToPosition, PositionToTime
- XAML Template cho Clip/Track
- ViewModel structure

---

### 2. **Smeedee (Timeline Widget)**

**URL:** [Smeedee GitHub](https://github.com/smeedee/Smeedee)

**Mô tả:** Dự án cũ nhưng code rất sạch, chuyên về hiển thị dữ liệu theo thời gian.

**Giá trị tham khảo:**
- ✅ **Virtualization** (chỉ render visible items) - VERY IMPORTANT
- ✅ DateTime → Pixel conversion logic
- ✅ Smooth scrolling implementation

**Cách tìm:**
```
GitHub search: "Smeedee.Widget.Timeline"
File cần xem: /source/Smeedee.Widgets/Smeedee.Widget.Timeline/
```

**Copy từ đây:**
- Virtualization pattern
- Time conversion algorithms
- ScrollViewer integration

---

### 3. **nGantt (WPF Gantt Chart)** ⭐ HIGHLY RELEVANT

**URL:** [nGantt GitHub](https://github.com/IvanKarpan/nGantt)

**Mô tả:** Gantt Chart (Project Management) - **90% giống Video Timeline về mặt kỹ thuật.**

**Giá trị tham khảo:**
- ✅ **Drag & Drop** (di chuyển bar)
- ✅ **Resize** (thay đổi kích thước clip)
- ✅ **Collision detection** (tránh chồng chéo)
- ✅ Snap-to-grid logic
- ✅ Time-based positioning

**Cách tìm:**
```
GitHub search: "nGantt WPF"
File cần xem: /GanttChartWPF/, /GanttChart.Converters/
```

**Copy từ đây:**
- Drag handler + Resize handler code
- Collision detection algorithm
- Thumb behavior binding
- Grid-based positioning

---

### 4. **NAudio WPF Samples**

**URL:** [NAudio GitHub](https://github.com/naudio/NAudio)

**Mô tả:** Audio processing library + WPF demo samples.

**Giá trị tham khảo:**
- ✅ **Waveform drawing** (vẽ sóng âm lên Canvas) - FAST & OPTIMIZED
- ✅ Real-time audio visualization
- ✅ Audio position sync with UI

**Cách tìm:**
```
GitHub search: "NAudio WPF"
File cần xem: /Demos/WPF/, /Demos/NAudioDemo/
```

**Copy từ đây:**
- Waveform rendering logic
- Audio position → UI update binding
- FFT visualization pattern (cho Spectrum)

---

## 🔧 WPF Native Controls - Cốt Lõi của Timeline

### Bắt buộc phải hiểu các Control sau:

#### 1. **Thumb**
```csharp
// Mục đích: Drag & Resize trong Timeline
// KHÔNG dùng Button hay Rectangle - hãy dùng Thumb
<Thumb DragDelta="Thumb_DragDelta" DragStarted="Thumb_DragStarted" />
```
- Cốt lõi để làm chức năng kéo clip (Drag)
- Cốt lõi để làm chức năng thay đổi độ dài clip (Resize)
- Tự động handle MouseDown, MouseMove, MouseUp events

#### 2. **ScrollViewer + IScrollInfo**
```csharp
// Mục đích: Cuộn ngang timeline vô tận, đồng bộ Ruler + Content
// IScrollInfo = custom scroll behavior (quan trọng!)
<ScrollViewer>
  <Canvas ItemsSource="{Binding Clips}" />
</ScrollViewer>
```
- Cho phép scroll ngang timeline
- Synchronize giữa Ruler (thước đo) và Content (clip area)
- Customize scroll speed/snap-to-grid

#### 3. **Canvas (hoặc Grid)**
```xaml
<!-- Mục đích: Định vị tuyệt đối vị trí Clip theo thời gian -->
<Canvas>
  <Rectangle Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}" />
</Canvas>
```
- Canvas.Left = thời gian start (tính theo pixel)
- Canvas.Top = track index (y position)

#### 4. **Behavior (Microsoft.Xaml.Behaviors)**
```csharp
// NuGet: Microsoft.Xaml.Behaviors.Wpf
// Mục đích: Gắn logic phức tạp vào UI mà không cần code-behind
<Thumb behaviors:DragBehavior.IsEnabled="True" />
```
- Giữ code-behind sạch sẽ
- MVVM-friendly
- Dễ test

---

## 🔍 GitHub Search Keywords

Nếu các project trên chưa đủ, hãy dùng các từ khóa này trên GitHub (filter language = C#):

| Từ khóa | Mục đích | Ví dụ |
|---------|---------|-------|
| `"WPF Audio Workstation"` | DAW (Digital Audio Workstation) | Reaper, Ableton-like interface |
| `"WPF DAW"` | Phần mềm làm nhạc - Timeline xử lý âm thanh | Music production tools |
| `"WPF Linear Video Editor"` | Video editor đơn giản | Timeline + basic editing |
| `"WPF Scheduler Control"` | Timeline kiểu lịch biểu | Cách xử lý thời gian |
| `"WPF Time Ruler"` | Chỉ riêng thước kẻ (00:00:00) | Hiển thị phút:giây:frame |
| `"WPF Timeline Virtualization"` | Performance optimization | Render chỉ visible area |
| `"WPF Clip Editor"` | Edit video clip | Drag, resize, transition |
| `language:csharp stars:>50"` | Filter: high-quality repo | Giới hạn kết quả chất lượng |

---

## 💡 Cách Copy Code (Best Practices)

### ❌ **KHÔNG NÊN:**
- Copy-paste toàn bộ project (dính quá nhiều dependency lạ)
- Copy classes 1:1 mà không hiểu logic

### ✅ **NÊN:**

**1. Công thức Tính Toán (Math)**
```csharp
// Copy từ họ
double pixelsPerSecond = TimelineWidth / TotalDuration;
double positionX = startTime * pixelsPerSecond;
double timeFromPosition = positionX / pixelsPerSecond;
```

**2. XAML Style & Template**
```xaml
<!-- Copy style cho Clip, Track, Ruler -->
<Style TargetType="local:ClipControl">
  <Setter Property="Template">
    <ControlTemplate>
      <Thumb x:Name="PART_Thumb" ... />
    </ControlTemplate>
  </Setter>
</Style>
```

**3. ViewModel Logic**
```csharp
// Copy cách họ lưu trữ Tracks & Clips
public ObservableCollection<TrackViewModel> Tracks { get; set; }
public ObservableCollection<ClipViewModel> Clips { get; set; }
```

**4. Behavior & Attachment**
```csharp
// Copy Drag/Resize handler pattern
public static class DragBehavior {
  public static void HandleDragDelta(...) { }
}
```

---

## 📋 Study Plan by Phase

### Phase 2 (Audio) - Optional
- Xem NAudio WPF Samples → Waveform drawing
- Xem Visualizer implementation

### Phase 3 (Timeline) - CRITICAL ⭐
1. Đọc **Gemini Timeline architecture** → Hiểu MVVM structure
2. Đọc **nGantt drag/resize** → Copy pattern
3. Đọc **Smeedee virtualization** → Performance
4. Đọc **NAudio waveform** → If adding audio visualization

### Phase 4 (AI & Automation)
- Có thể reference các project có API integration

---

## 🎯 When to Start Reference

- **ST-3 (Audio Service):** Reference NAudio WPF samples
- **ST-6 (MVP UI - Placeholder):** Reference Gemini architecture
- **Phase 3 (Timeline):** Deep dive into Gemini, nGantt, Smeedee

---

## 📝 Notes

- Đây là những project chất lượng cao (starred, maintained)
- Code họ viết năm 2010-2020, có thể bị cũ một chút, nhưng logic vẫn đúng
- Hãy lấy **logic** + **pattern**, không phải copy code 1:1
- Nếu project dùng .NET Framework 4.5, bạn sẽ cần update (bạn dùng .NET 8)

---

Last updated: 2026-02-06

When resuming Phase 3, start by cloning/downloading these repos and exploring their Timeline structure.
