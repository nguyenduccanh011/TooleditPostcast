using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PodcastVideoEditor.Core.Models
{
    /// <summary>
    /// Represents an editable property in the Property Editor panel.
    /// </summary>
    public partial class PropertyField : ObservableObject
    {
        /// <summary>
        /// Display name for the property.
        /// </summary>
        [ObservableProperty]
        private string name = string.Empty;

        /// <summary>
        /// Current value (boxed).
        /// </summary>
        [ObservableProperty]
        private object? value;

        /// <summary>
        /// Property type for UI control selection.
        /// </summary>
        public PropertyFieldType FieldType { get; set; }

        /// <summary>
        /// Logical group this property belongs to (Content, Font, Effects, Transform, etc.).
        /// </summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>
        /// Sort order within the group (lower = first).
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Name of a boolean property that controls visibility of this field.
        /// When set, this field is shown only when the named bool property is true.
        /// E.g. "HasShadow" hides ShadowColorHex when shadow is disabled.
        /// </summary>
        public string? VisibilityToggle { get; set; }

        /// <summary>
        /// Whether this field should be visible in the UI.
        /// Controlled by VisibilityToggle logic in PropertyEditorViewModel.
        /// </summary>
        [ObservableProperty]
        private bool isFieldVisible = true;

        /// <summary>
        /// Underlying property info for two-way binding.
        /// </summary>
        public System.Reflection.PropertyInfo? PropertyInfo { get; set; }

        /// <summary>
        /// Source element this property belongs to.
        /// </summary>
        public CanvasElement? SourceElement { get; set; }

        /// <summary>
        /// For numeric types: minimum value.
        /// </summary>
        public double? MinValue { get; set; }

        /// <summary>
        /// For numeric types: maximum value.
        /// </summary>
        public double? MaxValue { get; set; }

        /// <summary>
        /// When true, this slider's normalized 0–1 value is shown to the user as a percentage
        /// (0.5 → "50%"). Display-only — the underlying Value stays 0–1.
        /// </summary>
        public bool IsPercent { get; set; }

        /// <summary>
        /// Slider step/tick frequency. Defaults to 1 for whole-number fields.
        /// </summary>
        public double SliderStep { get; set; } = 1.0;

        /// <summary>
        /// Slider large-change step used for page-up/page-down style adjustments.
        /// </summary>
        public double SliderLargeChange { get; set; } = 1.0;

        /// <summary>
        /// For enum types: list of valid enum values.
        /// </summary>
        public IReadOnlyList<object>? EnumValues { get; set; }

        /// <summary>
        /// For composite controls (e.g. text formatting row), points to child fields.
        /// </summary>
        public IReadOnlyList<PropertyField>? CompositeFields { get; set; }

        /// <summary>
        /// When true, field participates in state sync but is not rendered in grouped UI.
        /// </summary>
        public bool ExcludeFromGrouping { get; set; }

        /// <summary>
        /// Group name for section headers (e.g. "Appearance", "Audio Response").
        /// Null or empty means ungrouped. Alias for Group.
        /// </summary>
        public string? GroupName
        {
            get => Group;
            set => Group = value ?? string.Empty;
        }

        /// <summary>
        /// True for numeric fields (Int/Float/Slider) that support +/- stepping via spinner buttons.
        /// </summary>
        public bool IsNumericStepper =>
            FieldType is PropertyFieldType.Int or PropertyFieldType.Float or PropertyFieldType.Slider;

        /// <summary>Increase the numeric value by one step (clamped to Min/Max).</summary>
        [RelayCommand]
        private void Increment() => Step(+1);

        /// <summary>Decrease the numeric value by one step (clamped to Min/Max).</summary>
        [RelayCommand]
        private void Decrement() => Step(-1);

        private void Step(int direction)
        {
            if (!IsNumericStepper)
                return;

            double step = SliderStep > 0 ? SliderStep : 1.0;
            if (!TryGetDouble(Value, out var current))
                current = MinValue ?? 0;

            double next = current + direction * step;
            if (MinValue.HasValue) next = Math.Max(MinValue.Value, next);
            if (MaxValue.HasValue) next = Math.Min(MaxValue.Value, next);
            next = Math.Round(next, DecimalsForStep(step), MidpointRounding.AwayFromZero);

            Value = FieldType == PropertyFieldType.Int ? (int)Math.Round(next) : next;
        }

        private static bool TryGetDouble(object? value, out double result)
        {
            switch (value)
            {
                case double d: result = d; return true;
                case float f: result = f; return true;
                case int i: result = i; return true;
                case null: result = 0; return false;
            }
            return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

        private static int DecimalsForStep(double step)
        {
            if (step >= 1)
                return 0;
            var text = step.ToString("0.########", CultureInfo.InvariantCulture);
            var dot = text.IndexOf('.');
            return dot < 0 ? 0 : text.Length - dot - 1;
        }
    }

    /// <summary>
    /// Represents a group of property fields, displayed as a collapsible section.
    /// </summary>
    public partial class PropertyGroupViewModel : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private bool isExpanded = true;

        [ObservableProperty]
        private int sortOrder;

        public System.Collections.ObjectModel.ObservableCollection<PropertyField> Fields { get; } = new();
    }

    /// <summary>
    /// UI control type for property editing.
    /// </summary>
    public enum PropertyFieldType
    {
        /// <summary>Single-line text input.</summary>
        String,

        /// <summary>Multiline text input.</summary>
        TextArea,

        /// <summary>Integer input.</summary>
        Int,

        /// <summary>Float/double input.</summary>
        Float,

        /// <summary>Hex color string (#RRGGBB).</summary>
        Color,

        /// <summary>Enum dropdown.</summary>
        Enum,

        /// <summary>Font family dropdown with font preview.</summary>
        FontFamily,

        /// <summary>Horizontal icon selector for text alignment.</summary>
        AlignmentRow,

        /// <summary>Horizontal row of text formatting toggles (B/I/U).</summary>
        FormattingRow,

        /// <summary>Single compact row for X/Y/Width/Height/Rotation.</summary>
        TransformRow,

        /// <summary>Boolean checkbox.</summary>
        Bool,

        /// <summary>Slider for numeric range.</summary>
        Slider
    }
}
