using CommunityToolkit.Mvvm.ComponentModel;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for the element property editor panel.
    /// Dynamically builds PropertyField list from selected CanvasElement via reflection,
    /// organized into collapsible groups (Content, Font, Effects, Transform, etc.).
    /// </summary>
    public partial class PropertyEditorViewModel : ObservableObject, IDisposable
    {
        // Reflection cache: per element type, cache the list of property descriptors
        private static readonly ConcurrentDictionary<Type, List<CachedPropertyDescriptor>> _reflectionCache = new();

        private sealed class CachedPropertyDescriptor
        {
            public required PropertyInfo PropertyInfo { get; init; }
            public required PropertyMetadataAttribute? Metadata { get; init; }
            public required string DisplayName { get; init; }
        }

        private static readonly string[] ExcludedProperties = { "Id", "Type", "CreatedAt", "IsSelected" };

        // Property name → (Group, SortOrder, VisibilityToggle)
        private static readonly Dictionary<string, (string Group, int Sort, string? Toggle)> PropertyGroupMap = new()
        {
            // Content group
            ["Content"]           = ("Content", 0, null),
            ["Text"]              = ("Content", 0, null),
            ["Style"]             = ("Content", 1, null),

            // Font group
            ["FontFamily"]        = ("Font", 0, null),
            ["FontSize"]          = ("Font", 1, null),
            ["ColorHex"]          = ("Font", 2, null),
            ["IsBold"]            = ("Font", 3, null),
            ["IsItalic"]          = ("Font", 4, null),
            ["IsUnderline"]       = ("Font", 5, null),
            ["Alignment"]         = ("Font", 6, null),
            ["LineHeight"]        = ("Font", 7, null),
            ["LetterSpacing"]     = ("Font", 8, null),

            // Shadow (Effects group)
            ["HasShadow"]         = ("Effects", 0, null),
            ["ShadowColorHex"]    = ("Effects", 1, "HasShadow"),
            ["ShadowOffsetX"]     = ("Effects", 2, "HasShadow"),
            ["ShadowOffsetY"]     = ("Effects", 3, "HasShadow"),
            ["ShadowBlur"]        = ("Effects", 4, "HasShadow"),

            // Outline (Effects group)
            ["HasOutline"]        = ("Effects", 10, null),
            ["OutlineColorHex"]   = ("Effects", 11, "HasOutline"),
            ["OutlineThickness"]  = ("Effects", 12, "HasOutline"),

            // Background (Effects group)
            ["HasBackground"]     = ("Effects", 20, null),
            ["BackgroundColorHex"]    = ("Effects", 21, "HasBackground"),
            ["BackgroundOpacity"]     = ("Effects", 22, "HasBackground"),
            ["BackgroundPadding"]     = ("Effects", 23, "HasBackground"),
            ["BackgroundCornerRadius"]= ("Effects", 24, "HasBackground"),

            // Transform group (collapsed by default)
            ["X"]                 = ("Transform", 0, null),
            ["Y"]                 = ("Transform", 1, null),
            ["Width"]             = ("Transform", 2, null),
            ["Height"]            = ("Transform", 3, null),
            ["ZIndex"]            = ("Transform", 4, null),
            ["Rotation"]          = ("Transform", 5, null),

            // Visibility & binding
            ["Name"]              = ("General", 0, null),
            ["IsVisible"]         = ("General", 1, null),
            ["SegmentId"]         = ("General", 2, null),

            // Image/Logo
            ["ImagePath"]         = ("Content", 0, null),
            ["FilePath"]          = ("Content", 0, null),
            ["Opacity"]           = ("Content", 1, null),
            ["ScaleMode"]         = ("Content", 2, null),

            // Visualizer
            ["ColorPalette"]      = ("Content", 0, null),
            ["BandCount"]         = ("Content", 1, null),
            ["SmoothingFactor"]   = ("Content", 2, null),
            ["ShowPeaks"]         = ("Content", 3, null),
            ["SymmetricMode"]     = ("Content", 4, null),
            ["PeakHoldTime"]      = ("Content", 5, null),
            ["BarWidth"]          = ("Content", 6, null),
            ["BarSpacing"]        = ("Content", 7, null),
        };

        // Group display order and default expansion state
        private static readonly Dictionary<string, (int Order, bool DefaultExpanded)> GroupOrder = new()
        {
            ["Content"]   = (0, true),
            ["Font"]      = (1, true),
            ["Effects"]   = (2, true),
            ["General"]   = (3, false),
            ["Transform"] = (4, false),
        };

        /// <summary>
        /// Called when a VisualizerElement's Style/ColorPalette/BandCount changes (for sync to VisualizerViewModel).
        /// </summary>
        public Action<VisualizerElement>? OnVisualizerElementConfigChanged { get; set; }

        [ObservableProperty]
        private CanvasElement? selectedElement;

        [ObservableProperty]
        private ObservableCollection<PropertyField> properties = new();

        /// <summary>
        /// Grouped property fields for the selected element, ordered by group sort order.
        /// </summary>
        public ObservableCollection<PropertyGroupViewModel> PropertyGroups { get; } = new();

        [ObservableProperty]
        private string headerText = "Element Properties";

        /// <summary>
        /// Compact timing badge text (e.g. "3.0s – 7.2s") when element is bound to a segment.
        /// Null when element has no segment binding.
        /// </summary>
        [ObservableProperty]
        private string? segmentTimingText;

        /// <summary>
        /// Segment that this element is bound to (via SegmentId). Used for timing badge display.
        /// </summary>
        private Segment? _boundSegment;

        private CanvasElement? _subscribedElement;
        private bool _disposed;
        private UndoRedoService? _undoRedo;

        // Debounce: coalesce rapid changes (slider drag) into a single undo step
        private string? _lastUndoPropName;
        private DateTime _lastUndoTime;
        private const int UndoCoalesceMs = 600;

        /// <summary>Wire undo/redo. Called from CanvasViewModel after construction.</summary>
        public void SetUndoRedoService(UndoRedoService? service) => _undoRedo = service;

        /// <summary>
        /// Set the bound segment for timing badge display.
        /// Call after SetSelectedElement when the element has a SegmentId.
        /// </summary>
        public void SetBoundSegment(Segment? segment)
        {
            if (_boundSegment != null)
                _boundSegment.PropertyChanged -= OnBoundSegmentPropertyChanged;

            _boundSegment = segment;

            if (segment != null)
            {
                segment.PropertyChanged += OnBoundSegmentPropertyChanged;
                UpdateTimingText(segment);
            }
            else
            {
                SegmentTimingText = null;
            }
        }

        private void OnBoundSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is Segment seg && e.PropertyName is nameof(Segment.StartTime) or nameof(Segment.EndTime))
                UpdateTimingText(seg);
        }

        private void UpdateTimingText(Segment segment)
        {
            SegmentTimingText = $"{segment.StartTime:F1}s \u2013 {segment.EndTime:F1}s  ({segment.SegmentDisplayDuration:F1}s)";
        }

        /// <summary>
        /// Call when CanvasViewModel.SelectedElement changes.
        /// </summary>
        public void SetSelectedElement(CanvasElement? element)
        {
            UnsubscribeFromElement();

            SelectedElement = element;
            _subscribedElement = element;

            if (element == null)
            {
                ClearPropertySubscriptions();
                Properties.Clear();
                PropertyGroups.Clear();
                HeaderText = "Element Properties";
                SetBoundSegment(null);
                return;
            }

            HeaderText = $"{element.Type}: {element.Name}";
            BuildPropertyFields(element);
            SubscribeToElement(element);
        }

        /// <summary>
        /// Update element property from PropertyField value (called when user edits in UI).
        /// </summary>
        public void ApplyValueToElement(PropertyField field, object? newValue)
        {
            if (field.SourceElement == null || field.PropertyInfo == null)
                return;

            try
            {
                var prop = field.PropertyInfo;
                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                object? oldValue = prop.GetValue(field.SourceElement);
                object? converted = ConvertValue(newValue, targetType);

                // Skip if the value didn't actually change
                if (Equals(oldValue, converted))
                    return;

                prop.SetValue(field.SourceElement, converted);

                // Record undo (coalesce rapid changes on the same property within debounce window)
                if (_undoRedo != null)
                {
                    var now = DateTime.UtcNow;
                    bool shouldCoalesce = _lastUndoPropName == prop.Name
                        && (now - _lastUndoTime).TotalMilliseconds < UndoCoalesceMs;

                    if (shouldCoalesce)
                    {
                        // Pop the previous action and merge old-value from it with new-value from this edit
                        if (_undoRedo.CanUndo)
                        {
                            _undoRedo.Undo(); // restores the previous old value
                            var mergedOld = prop.GetValue(field.SourceElement);
                            prop.SetValue(field.SourceElement, converted); // re-apply current value
                            _undoRedo.Record(new ElementPropertyChangedAction(field.SourceElement, prop, mergedOld, converted));
                        }
                    }
                    else
                    {
                        _undoRedo.Record(new ElementPropertyChangedAction(field.SourceElement, prop, oldValue, converted));
                    }

                    _lastUndoPropName = prop.Name;
                    _lastUndoTime = now;
                }

                // Sync VisualizerElement config changes to VisualizerViewModel
                if (field.SourceElement is VisualizerElement ve)
                {
                    OnVisualizerElementConfigChanged?.Invoke(ve);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to set property {Name}", field.Name);
            }
        }

        private void BuildPropertyFields(CanvasElement element)
        {
            ClearPropertySubscriptions();
            var descriptors = GetCachedDescriptors(element.GetType());

            Properties.Clear();
            PropertyGroups.Clear();

            var groupDict = new Dictionary<string, PropertyGroupViewModel>();

            foreach (var desc in descriptors)
            {
                var field = CreatePropertyField(element, desc);
                if (field != null)
                {
                    // Determine group: attribute > PropertyGroupMap > "General"
                    if (field.GroupName != null)
                    {
                        field.Group = field.GroupName;
                    }
                    else if (PropertyGroupMap.TryGetValue(desc.PropertyInfo.Name, out var mapMeta))
                    {
                        field.Group = mapMeta.Group;
                        field.SortOrder = mapMeta.Sort;
                        field.VisibilityToggle = mapMeta.Toggle;
                    }
                    else
                    {
                        field.Group = "General";
                        field.SortOrder = 99;
                    }

                    field.PropertyChanged += OnPropertyFieldValueChanged;
                    Properties.Add(field);

                    // Build group
                    if (!groupDict.TryGetValue(field.Group, out var group))
                    {
                        var (order, expanded) = GroupOrder.TryGetValue(field.Group, out var go) ? go : (99, true);
                        group = new PropertyGroupViewModel { Name = field.Group, SortOrder = order, IsExpanded = expanded };
                        groupDict[field.Group] = group;
                    }
                    group.Fields.Add(field);
                }
            }

            // Sort fields within each group, then add groups in order
            foreach (var group in groupDict.Values.OrderBy(g => g.SortOrder))
            {
                var sorted = group.Fields.OrderBy(f => f.SortOrder).ToList();
                group.Fields.Clear();
                foreach (var f in sorted)
                    group.Fields.Add(f);
                PropertyGroups.Add(group);
            }

            // Set initial visibility for toggle-dependent fields
            UpdateToggleVisibility(element);
        }

        private static List<CachedPropertyDescriptor> GetCachedDescriptors(Type type)
        {
            return _reflectionCache.GetOrAdd(type, t =>
            {
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<EditorHiddenAttribute>() == null);

                var list = new List<CachedPropertyDescriptor>();
                foreach (var p in props)
                {
                    list.Add(new CachedPropertyDescriptor
                    {
                        PropertyInfo = p,
                        Metadata = p.GetCustomAttribute<PropertyMetadataAttribute>(),
                        DisplayName = FormatPropertyName(p.Name)
                    });
                }
                return list;
            });
        }

        /// <summary>
        /// Updates IsFieldVisible for all fields that have a VisibilityToggle,
        /// based on the current boolean value of the toggle property.
        /// </summary>
        private void UpdateToggleVisibility(CanvasElement element)
        {
            foreach (var field in Properties)
            {
                if (string.IsNullOrEmpty(field.VisibilityToggle))
                    continue;

                var toggleProp = element.GetType().GetProperty(field.VisibilityToggle);
                if (toggleProp != null && toggleProp.GetValue(element) is bool toggleVal)
                    field.IsFieldVisible = toggleVal;
            }
        }

        private void OnPropertyFieldValueChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PropertyField.Value) || sender is not PropertyField field)
                return;
            ApplyValueToElement(field, field.Value);
        }

        private static PropertyField? CreatePropertyField(CanvasElement element, CachedPropertyDescriptor desc)
        {
            var prop = desc.PropertyInfo;
            var meta = desc.Metadata;
            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var value = prop.GetValue(element);

            var field = new PropertyField
            {
                Name = desc.DisplayName,
                Value = value,
                PropertyInfo = prop,
                SourceElement = element,
                GroupName = meta?.Group,
                SortOrder = meta?.Order ?? 600
            };

            if (propType == typeof(bool))
            {
                field.FieldType = PropertyFieldType.Bool;
            }
            else if (propType == typeof(int))
            {
                field.FieldType = PropertyFieldType.Int;
                if (meta?.IsSlider == true)
                {
                    field.MinValue = meta.MinValue;
                    field.MaxValue = meta.MaxValue;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "BandCount")
                {
                    field.MinValue = 32;
                    field.MaxValue = 128;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "PeakHoldTime")
                {
                    field.MinValue = 0;
                    field.MaxValue = 2000;
                    field.FieldType = PropertyFieldType.Slider;
                }
            }
            else if (propType == typeof(float))
            {
                field.FieldType = PropertyFieldType.Float;
                field.Value = (double)(float)(value ?? 0f);
                if (meta?.IsSlider == true)
                {
                    field.MinValue = meta.MinValue;
                    field.MaxValue = meta.MaxValue;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "SmoothingFactor")
                {
                    field.MinValue = 0;
                    field.MaxValue = 1;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "BarWidth")
                {
                    field.MinValue = 1;
                    field.MaxValue = 50;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "BarSpacing")
                {
                    field.MinValue = 0;
                    field.MaxValue = 20;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name is "ShadowOffsetX" or "ShadowOffsetY")
                {
                    field.MinValue = -30;
                    field.MaxValue = 30;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "ShadowBlur")
                {
                    field.MinValue = 0;
                    field.MaxValue = 25;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "OutlineThickness")
                {
                    field.MinValue = 0.5;
                    field.MaxValue = 20;
                    field.FieldType = PropertyFieldType.Slider;
                }
            }
            else if (propType == typeof(double))
            {
                field.FieldType = PropertyFieldType.Float;
                if (meta?.IsSlider == true)
                {
                    field.MinValue = meta.MinValue;
                    field.MaxValue = meta.MaxValue;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "FontSize")
                {
                    field.MinValue = 8;
                    field.MaxValue = 200;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name is "Opacity" or "BackgroundOpacity")
                {
                    field.MinValue = 0;
                    field.MaxValue = 1;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "LineHeight")
                {
                    field.MinValue = 0.5;
                    field.MaxValue = 5;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "LetterSpacing")
                {
                    field.MinValue = -20;
                    field.MaxValue = 100;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name is "BackgroundPadding" or "BackgroundCornerRadius")
                {
                    field.MinValue = 0;
                    field.MaxValue = prop.Name == "BackgroundCornerRadius" ? 50 : 100;
                    field.FieldType = PropertyFieldType.Slider;
                }
            }
            else if (propType.IsEnum)
            {
                field.FieldType = PropertyFieldType.Enum;
                field.EnumValues = Enum.GetValues(propType).Cast<object>().ToList();
            }
            else if (propType == typeof(string))
            {
                if (meta?.IsColor == true || prop.Name is "ColorHex" or "ShadowColorHex" or "OutlineColorHex" or "BackgroundColorHex" or "PrimaryColorHex")
                    field.FieldType = PropertyFieldType.Color;
                else if (meta?.IsTextArea == true || prop.Name is "Text" or "Content")
                    field.FieldType = PropertyFieldType.TextArea;
                else
                    field.FieldType = PropertyFieldType.String;
            }
            else
            {
                return null;
            }

            return field;
        }

        private static string FormatPropertyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value))
                return value;

            // Handle numeric conversions (e.g. double from Slider -> int)
            if (targetType == typeof(int) && value is IConvertible conv)
            {
                try { return Convert.ToInt32(conv); } catch { return 0; }
            }

            var str = value.ToString();
            if (string.IsNullOrEmpty(str) && targetType != typeof(string))
                return null;

            if (targetType == typeof(string))
                return str ?? string.Empty;

            if (targetType == typeof(bool))
                return bool.TryParse(str, out var b) ? b : false;

            if (targetType.IsEnum)
                return Enum.TryParse(targetType, str, true, out var e) ? e : Enum.ToObject(targetType, 0);

            if (targetType == typeof(int))
                return int.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : 0;

            if (targetType == typeof(double))
                return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

            if (targetType == typeof(float))
            {
                if (value is double dbl) return (float)dbl;
                return float.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            }

            return value;
        }

        private void SubscribeToElement(CanvasElement element)
        {
            element.PropertyChanged += OnElementPropertyChanged;
        }

        private void UnsubscribeFromElement()
        {
            if (_subscribedElement != null)
            {
                _subscribedElement.PropertyChanged -= OnElementPropertyChanged;
                _subscribedElement = null;
            }
        }

        private void OnElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not CanvasElement element)
                return;

            var field = Properties.FirstOrDefault(f => f.PropertyInfo?.Name == e.PropertyName);
            if (field != null)
            {
                var newVal = field.PropertyInfo?.GetValue(element);
                if (!Equals(field.Value, newVal))
                    field.Value = newVal;
            }

            // Update toggle visibility when a toggle property changes
            if (e.PropertyName is "HasShadow" or "HasOutline" or "HasBackground")
                UpdateToggleVisibility(element);

            if (e.PropertyName == nameof(CanvasElement.Name))
                HeaderText = $"{element.Type}: {element.Name}";
        }

        private void ClearPropertySubscriptions()
        {
            foreach (var field in Properties)
                field.PropertyChanged -= OnPropertyFieldValueChanged;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            UnsubscribeFromElement();
            SetBoundSegment(null);
            ClearPropertySubscriptions();
            Properties.Clear();
            PropertyGroups.Clear();
            SelectedElement = null;
            GC.SuppressFinalize(this);
        }
    }
}
