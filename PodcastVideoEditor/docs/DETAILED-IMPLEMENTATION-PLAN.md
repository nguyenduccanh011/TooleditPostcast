# Phase 2 Detailed Implementation Plan

**Date:** 2026-02-07  
**Status:** ST-7 ✅ & ST-8 ✅ Complete | ST-9/10/11 Planning Phase  
**Target:** Complete Phase 2 by Mar 7, 2026

---

## 📋 ST-9: Timeline Editor & Segment Manager (7 hours)

### High-Level Architecture
```
TimelineView.xaml
├── Ruler (timecode display)
├── Timeline Track
│   ├── Grid Background
│   ├── Segment Blocks (drag/resize)
│   └── Playhead (vertical line, red)
└── Scrollbar (horizontal)

TimelineViewModel
├── Segments (ObservableCollection<Segment>)
├── SelectedSegment
├── PlayheadPosition (synced to audio)
└── Commands (Add, Delete, Update, Duplicate, Clear)

SegmentEditorPanel.xaml
├── TextBox (description/script)
├── Image Picker Button
├── ColorPicker
├── Transition ComboBox
├── Duration Slider
└── Delete Button
```

### Detailed Implementation Steps

#### **Step 1: Create/Update Segment Model** (30 min)
- [x] Check if Segment exists in `Core/Models/Segment.cs` (from Phase 1 DB schema)
- [ ] Add new properties:
  - `Description` (string) - script text
  - `BackgroundImagePath` (string) - segment background
  - `SegmentColor` (string hex) - timeline block color
  - `TransitionType` (enum: Cut, Fade, SlideLeft, SlideRight, ZoomIn)
  - `TransitionDurationSeconds` (double, 0-3s)
- [ ] Add validation method: `ValidateSegment()` → bool
- [ ] Add Clone() method

**Models to Create/Modify:**
- `Core/Models/Segment.cs` (extend existing)
- `Core/Models/TransitionType.cs` (enum) - NEW

#### **Step 2: Create TimelineViewModel** (2 hours)
**File:** `Ui/ViewModels/TimelineViewModel.cs`

```csharp
public class TimelineViewModel : ObservableObject
{
    // Collections
    [ObservableProperty]
    private ObservableCollection<Segment> segments = new();
    
    [ObservableProperty]
    private Segment? selectedSegment;
    
    // Playback sync
    [ObservableProperty]
    private double playheadPosition = 0.0; // seconds
    
    [ObservableProperty]
    private double totalDuration = 0.0; // from audio
    
    // Zoom/Scale
    [ObservableProperty]
    private double timelineScale = 50.0; // pixels per second
    
    // UI State
    [ObservableProperty]
    private string statusMessage = "Ready";
    
    // Commands
    [RelayCommand]
    public void AddSegmentAtPlayhead();
    
    [RelayCommand]
    public void DeleteSelectedSegment();
    
    [RelayCommand]
    public void DuplicateSegment(Segment segment);
    
    [RelayCommand]
    public void ClearAllSegments();
    
    [RelayCommand]
    public void UpdateSegment(Segment segment);
    
    // Methods
    public void SyncWithAudio(double currentPosition, double duration);
    public bool ValidateSegments();
    public void SnapToGrid(Segment segment);
}
```

**Features:**
- Add segment at current playhead position
- Delete selected segment
- Duplicate segment
- Validate no overlapping segments
- Sync playhead with audio every 100ms

#### **Step 3: Create TimelineView.xaml** (1.5 hours)
**File:** `Ui/Views/TimelineView.xaml`

**Layout:**
```
Grid (3 rows)
├── Row 0: Ruler (timecode)
│   ├── Canvas (60px height)
│   └── Draw timecode every 5s
├── Row 1: Timeline Track (main content)
│   ├── ScrollViewer (horizontal scroll)
│   ├── Canvas
│   │   ├── Grid background
│   │   ├── Segment rectangles (ItemsControl)
│   │   └── Playhead (Canvas.Left = PlayheadPosition * Scale)
│   └── Code-behind handles drag/resize
└── Row 2: Scrollbar preview
    └── Horizontal scrollbar (linked to ScrollViewer)
```

**Timecode Format:**
- Display: 0:00, 0:05, 0:10, 0:15, etc. (every 5 seconds)
- Calculate X position: `time * timelineScale`
- Font: 10pt, gray color

**Segment Block Rendering:**
- Rectangle with segment color
- Text label: "0:30-1:00" (start-end in MM:SS)
- Resize handles on left/right edges
- Click → select segment

#### **Step 4: Create TimelineView Code-Behind** (1 hour)
**File:** `Ui/Views/TimelineView.xaml.cs`

**Responsibilities:**
```csharp
public partial class TimelineView : UserControl
{
    private Point _dragStartPoint;
    private Segment? _draggedSegment;
    private double _dragStartX;
    
    private void OnSegmentMouseDown(object sender, MouseButtonEventArgs e);
    // → Detect drag start, record initial position
    
    private void OnSegmentMouseMove(object sender, MouseEventArgs e);
    // → Calculate new position, update segment, redraw
    
    private void OnSegmentMouseUp(object sender, MouseButtonEventArgs e);
    // → Finalize drag, validate (no overlaps), commit
    
    private void OnCanvasLoaded(object sender, RoutedEventArgs e);
    // → Initialize ruler labels, attach event handlers
    
    private void UpdatePlayhead();
    // → Draw red playhead line at PlayheadPosition * Scale
    
    private void RedrawTimeline();
    // → Repaint all segments, playhead, ruler
}
```

#### **Step 5: Create SegmentEditorPanel.xaml** (1 hour)
**File:** `Ui/Views/SegmentEditorPanel.xaml`

**Layout:**
```
Grid (dark theme, 500px wide)
├── TextBlock "Segment Editor"
├── Separator
├── Grid (labels + inputs)
│   ├── "Description" → TextBox (multi-line, 100px height)
│   ├── "Image" → Button "Browse..." → Image preview (if selected)
│   ├── "Color" → ColorPicker
│   ├── "Transition" → ComboBox (Cut, Fade, Slide, Zoom)
│   ├── "Duration" → Slider (0-3s) + TextBlock (display 0.5s)
│   ├── "Start Time" → TextBlock (read-only, MM:SS)
│   ├── "End Time" → TextBlock (read-only, MM:SS)
│   └── "Delete" → Button (red background)
└── StatusBar (showing validation errors)
```

**Data Binding:**
```csharp
DataContext = TimelineViewModel
SelectedSegment binding (two-way)
Description = SelectedSegment.Description (TextBox)
Color = SelectedSegment.SegmentColor (ColorPicker)
TransitionType = SelectedSegment.TransitionType (ComboBox)
TransitionDuration = SelectedSegment.TransitionDurationSeconds (Slider)
```

### Acceptance Criteria
- [ ] Timeline displays segments with correct timing
- [ ] Playhead syncs with audio (±50ms tolerance)
- [ ] Can add segment at playhead position
- [ ] Can drag-resize segments to change duration
- [ ] Segment selection updates SegmentEditorPanel
- [ ] Edit properties → timeline updates
- [ ] No overlapping segments allowed
- [ ] Scroll large timelines smoothly
- [ ] Build succeeds (0 errors)

---

## 📊 ST-10: Canvas + Visualizer Integration (4 hours)

### Architecture
```
CanvasView.xaml
├── Canvas (@1920x1080)
├── ItemsControl (Elements)
│   ├── TitleElement → TextBlock
│   ├── LogoElement → Image
│   ├── VisualizerElement → Image (bitmap from VisualizerViewModel)
│   ├── ImageElement → Image
│   └── TextElement → TextBlock
└── Selection rectangle (visual feedback)

Integration Points:
VisualizerElement (in CanvasElementTypes.cs)
├── Dependency: VisualizerViewModel reference
├── Subscribe to CurrentBitmap changes
└── Render SKBitmap at (X, Y, Width, Height)

CanvasViewModel
├── Initialize VisualizerViewModel
└── Update canvas on visualizer frame change
```

### Detailed Implementation Steps

#### **Step 1: Extend VisualizerElement** (1 hour)
- [ ] Modify `VisualizerElement` in `Core/Models/CanvasElementTypes.cs`:
  - Add property: `VisualizerStyle` (enum - already in VisualizerConfig)
  - Add property: `ColorPalette` (enum)
  - Add method: `GetBitmap()` → SKBitmap (from VisualizerViewModel)

#### **Step 2: Create SKBitmap → BitmapSource Converter** (1 hour)
**File:** `Ui/Converters/SKBitmapToBitmapSourceConverter.cs`

```csharp
public class SKBitmapToBitmapSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SkiaSharp.SKBitmap bitmap)
            return null;
        
        // Convert SKBitmap to WPF BitmapSource
        // Use SkiaSharp.Views.Desktop utilities
        return bitmap.ToBitmapSource();
    }
}
```

#### **Step 3: Update CanvasView to Render Visualizer** (1.5 hours)
- [ ] Update `CanvasView.xaml.cs`:
  - Initialize `VisualizerViewModel` in OnCanvasLoaded
  - Subscribe to VisualizerViewModel.CurrentBitmap changes
  - Update canvas overlay layer with visualizer bitmap
  - Handle canvas resize: rescale visualizer rendering

- [ ] Update DataTemplate for VisualizerElement:
  ```xaml
  <Image Source="{Binding Bitmap, Converter={StaticResource SKBitmapConverter}}"
         Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}"
         Width="{Binding Width}" Height="{Binding Height}"/>
  ```

#### **Step 4: Performance Optimization** (0.5 hours)
- [ ] Add frame-skip logic:
  ```csharp
  private int _frameSkipCounter = 0;
  const int FRAME_SKIP = 1; // render every frame (set to 2 if lag)
  
  if (++_frameSkipCounter >= FRAME_SKIP)
  {
      UpdateCanvasVisualizerBitmap();
      _frameSkipCounter = 0;
  }
  ```

- [ ] Log performance metrics:
  - FPS (frames per second)
  - Memory usage (GC.GetTotalMemory)
  - Rendering latency

### Acceptance Criteria
- [ ] Visualizer renders on canvas during audio playback
- [ ] Element can be moved/resized
- [ ] 60fps target (or graceful degradation)
- [ ] Memory stable (<500MB for 10-min sessions)
- [ ] No crashes on long sessions
- [ ] Pause audio → visualizer freezes

---

## 🎨 ST-11: Element Property Editor Panel (5 hours)

### Architecture
```
MainWindow
├── Canvas (center)
├── CanvasView
├── Properties Panel (right side, 300px wide)
│   ├── PropertyEditorView (dynamic by element type)
│   └── Updates two-way bound to SelectedElement
```

### Implementation Steps

#### **Step 1: Create PropertyEditorViewModel** (1.5 hours)
**File:** `Ui/ViewModels/PropertyEditorViewModel.cs`

```csharp
public class PropertyEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private CanvasElement? selectedElement;
    
    [ObservableProperty]
    private ObservableCollection<PropertyField> visibleProperties = new();
    
    [RelayCommand]
    public void UpdateProperty(string propertyName, object value);
    
    public void RefreshPropertyFields();
    // Generate fields based on selectedElement.Type
}
```

#### **Step 2: Create PropertyField Model** (0.5 hours)
**File:** `Ui/Models/PropertyField.cs`

```csharp
public class PropertyField
{
    public string DisplayName { get; set; }
    public string PropertyName { get; set; }
    public PropertyFieldType FieldType { get; set; } // Text, Color, Slider, etc.
    public object CurrentValue { get; set; }
    public object MinValue { get; set; }
    public object MaxValue { get; set; }
    public List<object> Options { get; set; } // for ComboBox
}

public enum PropertyFieldType
{
    TextBox,
    Slider,
    ColorPicker,
    ComboBox,
    CheckBox,
    FilePicker
}
```

#### **Step 3: Create PropertyEditorView.xaml** (2 hours)
**File:** `Ui/Views/PropertyEditorView.xaml`

**Dynamic Layout Generator:**
```csharp
// Code-behind generates controls based on PropertyField collection
foreach (var field in visibleProperties)
{
    switch (field.FieldType)
    {
        case PropertyFieldType.TextBox:
            GenerateTextBox(field);
            break;
        case PropertyFieldType.Slider:
            GenerateSlider(field);
            break;
        case PropertyFieldType.ColorPicker:
            GenerateColorPicker(field);
            break;
        // ... etc
    }
}
```

**UI Structure:**
```
Grid (dark theme, 300px wide)
├── TextBlock "Element Properties"
├── Separator
├── ScrollViewer
│   └── StackPanel (ItemsSource=VisibleProperties)
│       └── DataTemplate (generates controls dynamically)
│           ├── Label: PropertyName
│           └── Control: [TextBox|Slider|ColorPicker|etc]
└── Button "Reset to Default" (bottom)
```

#### **Step 4: Generate Property Fields by Element Type** (1 hour)
```csharp
private void RefreshPropertyFields()
{
    VisibleProperties.Clear();
    
    if (SelectedElement is TitleElement title)
    {
        VisibleProperties.Add(new PropertyField 
        { 
            DisplayName = "Text", 
            PropertyName = nameof(TitleElement.Text), 
            FieldType = PropertyFieldType.TextBox,
            CurrentValue = title.Text 
        });
        
        VisibleProperties.Add(new PropertyField 
        { 
            DisplayName = "Font Size", 
            PropertyName = nameof(TitleElement.FontSize), 
            FieldType = PropertyFieldType.Slider,
            CurrentValue = title.FontSize,
            MinValue = 8,
            MaxValue = 72
        });
        
        // Add more properties...
    }
    else if (SelectedElement is VisualizerElement viz)
    {
        VisibleProperties.Add(new PropertyField 
        { 
            DisplayName = "Style", 
            PropertyName = nameof(VisualizerElement.Style), 
            FieldType = PropertyFieldType.ComboBox,
            Options = new List<object> { VisualizerStyle.Bars, VisualizerStyle.Waveform, VisualizerStyle.Circular }
        });
        
        // Add more properties...
    }
}
```

#### **Step 5: Two-Way Data Binding** (1 hour)
- [ ] Binding Strategy:
  ```xaml
  <!-- For TextBox -->
  <TextBox Text="{Binding CurrentValue, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
           TextChanged="OnPropertyChanged"/>
  
  <!-- For Slider -->
  <Slider Value="{Binding CurrentValue, Mode=TwoWay}"/>
  
  <!-- For ColorPicker -->
  <local:ColorPicker Color="{Binding CurrentValue, Mode=TwoWay}"/>
  ```

- [ ] UpdateProperty command handler:
  ```csharp
  [RelayCommand]
  public void UpdateProperty(string propertyName, object value)
  {
      if (SelectedElement == null) return;
      
      var property = SelectedElement.GetType().GetProperty(propertyName);
      property?.SetValue(SelectedElement, value);
      
      LogMessage($"Property {propertyName} updated");
  }
  ```

### Property Fields by Element Type

| Element | Properties |
|---------|-----------|
| **TitleElement** | Text, FontFamily, FontSize, Color, Bold, Italic, Alignment |
| **LogoElement** | ImagePath, Opacity, ScaleMode, Rotation |
| **VisualizerElement** | Style, Palette, BandCount, SmoothingFactor |
| **ImageElement** | FilePath, Opacity, ScaleMode, CropRect |
| **TextElement** | Content, FontFamily, FontSize, Color, Alignment |
| **ALL** | X, Y, Width, Height, ZIndex, Name, IsVisible |

### Acceptance Criteria
- [ ] Properties panel shows when element selected
- [ ] Different fields for each element type
- [ ] TextBox → multi-line for descriptions
- [ ] Sliders for numeric ranges (FontSize, Opacity, etc.)
- [ ] ColorPicker for colors
- [ ] ComboBox for enums (fonts, styles, etc.)
- [ ] FilePicker for image selection
- [ ] Two-way binding (edit field → element updates)
- [ ] Element changes → properties refresh
- [ ] "Reset to Default" button works

---

## 📈 Overall Timeline & Effort Estimate

```
ST-9: Timeline Editor         [7h]   - Feb 7 afternoon
ST-10: Canvas Integration     [4h]   - Feb 8 morning
ST-11: Property Panel         [5h]   - Feb 8 afternoon/evening
────────────────────────────────────
TOTAL PHASE 2 REMAINING:      [16h]  - ~2 days work

Phase 2 COMPLETION TARGET: Feb 8-9, 2026
Phase 3: Script & Automation ready for ~Feb 10
```

---

## 🎯 Key Success Metrics

✅ **All 3 tasks compiling (0 errors, <10 warnings)**  
✅ **Timeline synced within ±50ms of audio**  
✅ **Visualizer rendering at 60fps on canvas**  
✅ **Property editor updates all element types**  
✅ **Memory stable (<500MB)**  
✅ **No crashes on long sessions (10+ minutes)**  
✅ **Ready for Phase 3 implementation**

---

**Status:** Ready for approval before implementation 🚀
