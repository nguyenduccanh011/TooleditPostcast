using CommunityToolkit.Mvvm.ComponentModel;
using PodcastVideoEditor.Core.Models;
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
    /// Dynamically builds PropertyField list from selected CanvasElement via reflection.
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

        /// <summary>
        /// Called when a VisualizerElement's Style/ColorPalette/BandCount changes (for sync to VisualizerViewModel).
        /// </summary>
        public Action<VisualizerElement>? OnVisualizerElementConfigChanged { get; set; }

        [ObservableProperty]
        private CanvasElement? selectedElement;

        [ObservableProperty]
        private ObservableCollection<PropertyField> properties = new();

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
                HeaderText = "Element Properties";
                SetBoundSegment(null);
                return;
            }

            HeaderText = $"{element.Type}: {element.Name}";
            BuildPropertyFields(element);
            SubscribeToElement(element);
        }

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
            SegmentTimingText = $"{segment.StartTime:F1}s – {segment.EndTime:F1}s  ({segment.SegmentDisplayDuration:F1}s)";
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
                object? converted = ConvertValue(newValue, targetType);
                prop.SetValue(field.SourceElement, converted);

                // Sync any VisualizerElement property change to VisualizerViewModel
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

            var fields = new List<PropertyField>();
            foreach (var desc in descriptors)
            {
                var field = CreatePropertyField(element, desc);
                if (field != null)
                {
                    field.PropertyChanged += OnPropertyFieldValueChanged;
                    fields.Add(field);
                }
            }

            // Sort by group order then sort order within group
            fields.Sort((a, b) =>
            {
                int cmp = a.SortOrder.CompareTo(b.SortOrder);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            Properties.Clear();
            foreach (var f in fields)
                Properties.Add(f);
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

            // Apply min/max from attribute
            if (meta != null)
            {
                if (!double.IsNaN(meta.MinValue)) field.MinValue = meta.MinValue;
                if (!double.IsNaN(meta.MaxValue)) field.MaxValue = meta.MaxValue;
            }

            // Determine FieldType: attribute override > attribute hints > type inference
            if (meta?.FieldTypeOverride != null)
            {
                field.FieldType = meta.FieldTypeOverride.Value;
            }
            else if (propType == typeof(bool))
            {
                field.FieldType = PropertyFieldType.Bool;
            }
            else if (propType == typeof(int))
            {
                field.FieldType = meta is { IsSlider: true } ? PropertyFieldType.Slider : PropertyFieldType.Int;
            }
            else if (propType == typeof(float))
            {
                field.FieldType = meta is { IsSlider: true } ? PropertyFieldType.Slider : PropertyFieldType.Float;
                field.Value = (double)(float)(value ?? 0f);
            }
            else if (propType == typeof(double))
            {
                field.FieldType = meta is { IsSlider: true } ? PropertyFieldType.Slider : PropertyFieldType.Float;
            }
            else if (propType.IsEnum)
            {
                field.FieldType = PropertyFieldType.Enum;
                field.EnumValues = Enum.GetValues(propType).Cast<object>().ToList();
            }
            else if (propType == typeof(string))
            {
                if (meta is { IsColor: true })
                    field.FieldType = PropertyFieldType.Color;
                else if (meta is { IsTextArea: true })
                    field.FieldType = PropertyFieldType.TextArea;
                else
                    field.FieldType = PropertyFieldType.String;
            }
            else
            {
                return null; // Unsupported property type
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
            SelectedElement = null;
            GC.SuppressFinalize(this);
        }
    }
}
