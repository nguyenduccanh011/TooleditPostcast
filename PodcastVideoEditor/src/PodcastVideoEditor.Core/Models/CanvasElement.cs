using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace PodcastVideoEditor.Core.Models
{
    /// <summary>
    /// Base class for all canvas elements (Title, Logo, Image, Visualizer, Text).
    /// Represents an element that can be placed and manipulated on a visual canvas.
    /// </summary>
    public abstract class CanvasElement : ObservableObject
    {
        private double _x;
        private double _y;
        private double _width;
        private double _height;
        private int _zIndex;
        private bool _isSelected;
        private bool _isVisible = true;
        private string _name;

        /// <summary>
        /// Unique identifier for the element.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Type of element (Title, Logo, Visualizer, Image, Text).
        /// </summary>
        public abstract ElementType Type { get; }

        /// <summary>
        /// Display name for the element.
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// X position in pixels (absolute positioning).
        /// </summary>
        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        /// <summary>
        /// Y position in pixels (absolute positioning).
        /// </summary>
        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        /// <summary>
        /// Width in pixels.
        /// </summary>
        public double Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        /// <summary>
        /// Height in pixels.
        /// </summary>
        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        /// <summary>
        /// Z-index for layering (higher = on top).
        /// Range: -100 to +100.
        /// </summary>
        public int ZIndex
        {
            get => _zIndex;
            set => SetProperty(ref _zIndex, Math.Clamp(value, -100, 100));
        }

        /// <summary>
        /// Whether this element is currently selected.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// Whether this element is visible on canvas.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        /// <summary>
        /// Timestamp when element was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optional: ID of the segment this element is attached to. When set, element is only shown during segment's [StartTime, EndTime].
        /// Null = global overlay (always visible).
        /// </summary>
        public string? SegmentId { get; set; }

        /// <summary>
        /// Validate the element's properties.
        /// Returns true if valid, false otherwise.
        /// </summary>
        public virtual bool Validate()
        {
            // Check bounds
            if (Width <= 0 || Height <= 0)
                return false;

            if (X < 0 || Y < 0)
                return false;

            return true;
        }

        /// <summary>
        /// Reset element to default values.
        /// </summary>
        public virtual void ResetToDefault()
        {
            X = 0;
            Y = 0;
            Width = 200;
            Height = 100;
            ZIndex = 0;
            IsVisible = true;
            IsSelected = false;
        }

        /// <summary>
        /// Create a deep copy of this element.
        /// </summary>
        public abstract CanvasElement Clone();

        /// <summary>
        /// Get a string representation of this element.
        /// </summary>
        public override string ToString() =>
            $"{Type}: {Name} @ ({X}, {Y}) [{Width}x{Height}] Z:{ZIndex}";
    }

    /// <summary>
    /// Enumeration of supported canvas element types.
    /// </summary>
    public enum ElementType
    {
        Title,
        Logo,
        Visualizer,
        Image,
        Text
    }
}
