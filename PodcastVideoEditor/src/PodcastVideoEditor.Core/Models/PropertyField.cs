using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

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
        /// For enum types: list of valid enum values.
        /// </summary>
        public IReadOnlyList<object>? EnumValues { get; set; }

        /// <summary>
        /// Whether this property is read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }
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

        /// <summary>Boolean checkbox.</summary>
        Bool,

        /// <summary>Slider for numeric range.</summary>
        Slider
    }
}
