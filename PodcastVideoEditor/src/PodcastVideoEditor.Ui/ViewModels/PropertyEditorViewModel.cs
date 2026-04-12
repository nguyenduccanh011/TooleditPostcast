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

        /// <summary>
        /// Called whenever a selected element property value is changed via the property editor.
        /// Used by container view models to schedule persistence.
        /// </summary>
        public Action<CanvasElement, string>? OnElementPropertyEdited { get; set; }

        /// <summary>
        /// Called when a TextOverlayElement property changes, to propagate to track siblings.
        /// Parameters: (TextOverlayElement source, string propertyName).
        /// Returns: list of (siblingElement, oldValue) pairs for undo support — null if no propagation.
        /// </summary>
        public Func<TextOverlayElement, string, List<(CanvasElement Element, object? OldValue)>?>? OnTextElementPropertyChanged { get; set; }

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
        /// Informational text shown when a TextOverlayElement is on a text track with shared style.
        /// Null when element is not part of a shared-style text track.
        /// </summary>
        [ObservableProperty]
        private string? trackStyleInfoText;

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
                object? normalizedValue = NormalizeSliderInput(field, newValue);
                object? converted = ConvertValue(normalizedValue, targetType);

                // Skip if the value didn't actually change
                if (Equals(oldValue, converted))
                    return;

                prop.SetValue(field.SourceElement, converted);

                // Propagate TextOverlayElement property changes to track siblings
                // (must happen before undo recording so we can capture sibling old values)
                List<(CanvasElement Element, object? OldValue)>? siblingChanges = null;
                if (field.SourceElement is TextOverlayElement te && !string.IsNullOrEmpty(prop.Name))
                {
                    siblingChanges = OnTextElementPropertyChanged?.Invoke(te, prop.Name);
                }

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
                            _undoRedo.Undo(); // restores the previous old value (and siblings if CompoundAction)
                            var mergedOld = prop.GetValue(field.SourceElement);
                            prop.SetValue(field.SourceElement, converted); // re-apply current value

                            // Re-propagate siblings after coalesce
                            if (field.SourceElement is TextOverlayElement te2 && !string.IsNullOrEmpty(prop.Name))
                                siblingChanges = OnTextElementPropertyChanged?.Invoke(te2, prop.Name);

                            RecordPropertyUndoWithSiblings(field.SourceElement, prop, mergedOld, converted, siblingChanges);
                        }
                    }
                    else
                    {
                        RecordPropertyUndoWithSiblings(field.SourceElement, prop, oldValue, converted, siblingChanges);
                    }

                    _lastUndoPropName = prop.Name;
                    _lastUndoTime = now;
                }

                // Sync VisualizerElement config changes to VisualizerViewModel
                if (field.SourceElement is VisualizerElement ve)
                {
                    OnVisualizerElementConfigChanged?.Invoke(ve);
                }

                OnElementPropertyEdited?.Invoke(field.SourceElement, prop.Name);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to set property {Name}", field.Name);
            }
        }

        private static object? NormalizeSliderInput(PropertyField field, object? value)
        {
            if (field.FieldType != PropertyFieldType.Slider || value == null)
                return value;

            if (!TryConvertToDouble(value, out var numeric))
                return value;

            var step = field.SliderStep > 0 ? field.SliderStep : 0;
            if (step > 0)
            {
                if (field.MinValue.HasValue)
                    numeric = field.MinValue.Value + Math.Round((numeric - field.MinValue.Value) / step) * step;
                else
                    numeric = Math.Round(numeric / step) * step;
            }

            if (field.MinValue.HasValue)
                numeric = Math.Max(field.MinValue.Value, numeric);
            if (field.MaxValue.HasValue)
                numeric = Math.Min(field.MaxValue.Value, numeric);

            // Stabilize floating-point noise from slider math (e.g. 0.95000000005).
            var decimals = GetStepDecimalPlaces(step);
            numeric = Math.Round(numeric, decimals, MidpointRounding.AwayFromZero);
            return numeric;
        }

        private static bool TryConvertToDouble(object value, out double numeric)
        {
            if (value is double d)
            {
                numeric = d;
                return true;
            }

            if (value is float f)
            {
                numeric = f;
                return true;
            }

            if (value is IConvertible conv)
            {
                try
                {
                    numeric = conv.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    // Fall through to parse branch.
                }
            }

            var text = value.ToString();
            return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out numeric)
                || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out numeric);
        }

        private static int GetStepDecimalPlaces(double step)
        {
            if (step <= 0)
                return 4;

            var text = step.ToString("0.################", CultureInfo.InvariantCulture);
            var dotIndex = text.IndexOf('.');
            if (dotIndex < 0)
                return 0;

            return Math.Max(0, text.Length - dotIndex - 1);
        }

        /// <summary>
        /// Record an undo action for a property change, including sibling changes as a CompoundAction when applicable.
        /// </summary>
        private void RecordPropertyUndoWithSiblings(
            CanvasElement source,
            System.Reflection.PropertyInfo prop,
            object? oldValue,
            object? newValue,
            List<(CanvasElement Element, object? OldValue)>? siblingChanges)
        {
            if (_undoRedo == null) return;

            var sourceAction = new ElementPropertyChangedAction(source, prop, oldValue, newValue);

            if (siblingChanges == null || siblingChanges.Count == 0)
            {
                _undoRedo.Record(sourceAction);
                return;
            }

            // Build compound action: source + all sibling changes
            var actions = new List<IUndoableAction> { sourceAction };
            foreach (var (sibling, sibOld) in siblingChanges)
            {
                actions.Add(new ElementPropertyChangedAction(sibling, prop, sibOld, newValue));
            }
            _undoRedo.Record(new CompoundAction($"Change '{prop.Name}' on track text elements", actions));
        }

        private void BuildPropertyFields(CanvasElement element)
        {
            ClearPropertySubscriptions();
            var descriptors = GetCachedDescriptors(element.GetType());

            Properties.Clear();
            PropertyGroups.Clear();

            var fields = new List<PropertyField>();

            foreach (var desc in descriptors)
            {
                var field = CreatePropertyField(element, desc);
                if (field == null)
                    continue;

                // Group and order are fully driven by [PropertyMetadata] attribute.
                // VisibilityToggle is also read from attribute.
                if (string.IsNullOrEmpty(field.Group))
                    field.Group = "General";

                if (desc.Metadata?.VisibilityToggle != null)
                    field.VisibilityToggle = desc.Metadata.VisibilityToggle;

                field.PropertyChanged += OnPropertyFieldValueChanged;
                fields.Add(field);
            }

            // Add compact B/I/U row for text overlays while preserving backing fields
            // for undo, propagation, and external sync.
            if (element is TextOverlayElement)
                AddFormattingCompositeField(fields, element);

            AddTransformCompositeField(fields, element);

            foreach (var field in fields)
                Properties.Add(field);

            var groupDict = new Dictionary<string, PropertyGroupViewModel>();

            foreach (var field in fields)
            {
                if (field.ExcludeFromGrouping)
                    continue;

                // Build group
                if (!groupDict.TryGetValue(field.Group, out var group))
                {
                    var (order, expanded) = GroupOrder.TryGetValue(field.Group, out var go) ? go : (99, true);
                    group = new PropertyGroupViewModel { Name = field.Group, SortOrder = order, IsExpanded = expanded };
                    groupDict[field.Group] = group;
                }
                group.Fields.Add(field);
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

        private static void AddFormattingCompositeField(List<PropertyField> fields, CanvasElement element)
        {
            var boldField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(TextOverlayElement.IsBold));
            var italicField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(TextOverlayElement.IsItalic));
            var underlineField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(TextOverlayElement.IsUnderline));

            if (boldField == null || italicField == null || underlineField == null)
                return;

            boldField.ExcludeFromGrouping = true;
            italicField.ExcludeFromGrouping = true;
            underlineField.ExcludeFromGrouping = true;

            var formattingField = new PropertyField
            {
                Name = "Pattern",
                FieldType = PropertyFieldType.FormattingRow,
                Group = boldField.Group,
                SortOrder = Math.Min(boldField.SortOrder, Math.Min(italicField.SortOrder, underlineField.SortOrder)),
                SourceElement = element,
                CompositeFields = new[] { boldField, italicField, underlineField }
            };

            fields.Add(formattingField);
        }

        private static void AddTransformCompositeField(List<PropertyField> fields, CanvasElement element)
        {
            var xField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(CanvasElement.X));
            var yField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(CanvasElement.Y));
            var widthField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(CanvasElement.Width));
            var heightField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(CanvasElement.Height));
            var rotationField = fields.FirstOrDefault(f => f.PropertyInfo?.Name == nameof(CanvasElement.Rotation));

            if (xField == null || yField == null || widthField == null || heightField == null || rotationField == null)
                return;

            xField.ExcludeFromGrouping = true;
            yField.ExcludeFromGrouping = true;
            widthField.ExcludeFromGrouping = true;
            heightField.ExcludeFromGrouping = true;
            rotationField.ExcludeFromGrouping = true;

            var transformField = new PropertyField
            {
                Name = "Transform",
                FieldType = PropertyFieldType.TransformRow,
                Group = xField.Group,
                SortOrder = Math.Min(xField.SortOrder,
                            Math.Min(yField.SortOrder,
                            Math.Min(widthField.SortOrder,
                            Math.Min(heightField.SortOrder, rotationField.SortOrder)))),
                SourceElement = element,
                CompositeFields = new[] { xField, yField, widthField, heightField, rotationField }
            };

            fields.Add(transformField);
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

            // All field-type inference is now driven by [PropertyMetadata] attributes on model properties.
            // No hard-coded property name fallbacks needed — attributes are the single source of truth.

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
                    field.SliderStep = 1.0;
                    field.SliderLargeChange = 1.0;
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
                    ConfigureDecimalSlider(field, meta.MinValue, meta.MaxValue);
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
                    ConfigureDecimalSlider(field, meta.MinValue, meta.MaxValue);
                    field.FieldType = PropertyFieldType.Slider;
                }
            }
            else if (propType.IsEnum)
            {
                if (element is TextOverlayElement && prop.Name == nameof(TextOverlayElement.Alignment))
                    field.FieldType = PropertyFieldType.AlignmentRow;
                else
                    field.FieldType = PropertyFieldType.Enum;
                field.EnumValues = Enum.GetValues(propType).Cast<object>().ToList();
            }
            else if (propType == typeof(string))
            {
                if (meta?.IsColor == true)
                    field.FieldType = PropertyFieldType.Color;
                else if (meta?.IsTextArea == true)
                    field.FieldType = PropertyFieldType.TextArea;
                else if (element is TextOverlayElement && prop.Name == nameof(TextOverlayElement.FontFamily))
                    field.FieldType = PropertyFieldType.FontFamily;
                else
                    field.FieldType = PropertyFieldType.String;
            }
            else
            {
                return null;
            }

            return field;
        }

        private static void ConfigureDecimalSlider(PropertyField field, double min, double max)
        {
            var range = Math.Abs(max - min);

            if (range <= 1.0)
                field.SliderStep = 0.01;
            else if (range <= 10.0)
                field.SliderStep = 0.1;
            else
                field.SliderStep = 1.0;

            field.SliderLargeChange = field.SliderStep * 10.0;
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
