using System;

namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Declares how a CanvasElement property should appear in the Property Editor panel.
/// Applied to public properties to control grouping, ordering, field type, and numeric ranges.
/// Properties without this attribute use type-based defaults.
/// Properties marked [EditorHidden] are excluded entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PropertyMetadataAttribute : Attribute
{
    /// <summary>
    /// Group header text (e.g. "🎨 Appearance", "📊 Frequency Bars").
    /// Null means ungrouped.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Sort order — lower values appear first. Properties within the same group are sorted by this.
    /// </summary>
    public int Order { get; set; } = 600;

    /// <summary>
    /// Override the default UI field type. When null, field type is inferred from property type.
    /// </summary>
    public PropertyFieldType? FieldTypeOverride { get; set; }

    /// <summary>
    /// Minimum value for numeric/slider fields.
    /// </summary>
    public double MinValue { get; set; } = double.NaN;

    /// <summary>
    /// Maximum value for numeric/slider fields.
    /// </summary>
    public double MaxValue { get; set; } = double.NaN;

    /// <summary>
    /// If true, inferred FieldType becomes PropertyFieldType.Slider (for numeric properties).
    /// </summary>
    public bool IsSlider { get; set; }

    /// <summary>
    /// If true, string property is rendered as PropertyFieldType.Color.
    /// </summary>
    public bool IsColor { get; set; }

    /// <summary>
    /// If true, string property is rendered as PropertyFieldType.TextArea.
    /// </summary>
    public bool IsTextArea { get; set; }
}

/// <summary>
/// Marks a property as hidden from the Property Editor panel.
/// Replaces hardcoded exclusion arrays.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class EditorHiddenAttribute : Attribute
{
}
