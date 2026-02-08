using CommunityToolkit.Mvvm.ComponentModel;
using PodcastVideoEditor.Core.Models;
using System;
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
        private static readonly string[] ExcludedProperties = { "Id", "Type", "CreatedAt", "IsSelected" };

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
                object? converted = ConvertValue(newValue, targetType);
                prop.SetValue(field.SourceElement, converted);

                // Sync VisualizerElement Style/ColorPalette/BandCount to VisualizerViewModel
                if (field.SourceElement is VisualizerElement ve &&
                    (prop.Name == "Style" || prop.Name == "ColorPalette" || prop.Name == "BandCount"))
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
            var type = element.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && !ExcludedProperties.Contains(p.Name));

            Properties.Clear();
            foreach (var prop in props)
            {
                var field = CreatePropertyField(element, prop);
                if (field != null)
                {
                    field.PropertyChanged += OnPropertyFieldValueChanged;
                    Properties.Add(field);
                }
            }
        }

        private void OnPropertyFieldValueChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PropertyField.Value) || sender is not PropertyField field)
                return;
            ApplyValueToElement(field, field.Value);
        }

        private static PropertyField? CreatePropertyField(CanvasElement element, PropertyInfo prop)
        {
            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var value = prop.GetValue(element);

            var field = new PropertyField
            {
                Name = FormatPropertyName(prop.Name),
                Value = value,
                PropertyInfo = prop,
                SourceElement = element
            };

            if (propType == typeof(bool))
            {
                field.FieldType = PropertyFieldType.Bool;
            }
            else if (propType == typeof(int))
            {
                field.FieldType = PropertyFieldType.Int;
                if (prop.Name == "BandCount")
                {
                    field.MinValue = 32;
                    field.MaxValue = 128;
                    field.FieldType = PropertyFieldType.Slider;
                }
            }
            else if (propType == typeof(double))
            {
                field.FieldType = PropertyFieldType.Float;
                if (prop.Name == "FontSize")
                {
                    field.MinValue = 8;
                    field.MaxValue = prop.Name == "FontSize" && element is TitleElement ? 200 : 100;
                    field.FieldType = PropertyFieldType.Slider;
                }
                else if (prop.Name == "Opacity")
                {
                    field.MinValue = 0;
                    field.MaxValue = 1;
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
                if (prop.Name == "ColorHex")
                    field.FieldType = PropertyFieldType.Color;
                else if (prop.Name is "Text" or "Content")
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
            ClearPropertySubscriptions();
            Properties.Clear();
            SelectedElement = null;
            GC.SuppressFinalize(this);
        }
    }
}
