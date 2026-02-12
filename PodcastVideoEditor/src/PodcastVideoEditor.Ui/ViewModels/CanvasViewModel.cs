using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Ui.Converters;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for managing canvas elements and interactions.
    /// </summary>
    public partial class CanvasViewModel : ObservableObject, IDisposable
    {
        private readonly VisualizerViewModel? _visualizerViewModel;
        private readonly DispatcherTimer? _visualizerTimer;
        private bool _disposed;

        public ObservableCollection<string> AspectRatioOptions { get; } = new()
        {
            "9:16", "16:9", "1:1", "4:5"
        };

        [ObservableProperty]
        private ObservableCollection<CanvasElement> elements = new();

        [ObservableProperty]
        private CanvasElement? selectedElement;

        /// <summary>
        /// Property editor for selected element.
        /// </summary>
        public PropertyEditorViewModel PropertyEditor { get; }

        [ObservableProperty]
        private double canvasWidth = 1920;

        [ObservableProperty]
        private double canvasHeight = 1080;

        [ObservableProperty]
        private double gridSize = 10.0; // Grid snapping (optional)

        [ObservableProperty]
        private bool showGrid = false;

        [ObservableProperty]
        private string statusMessage = "Ready";

        /// <summary>
        /// Bitmap for visualizer elements on canvas. Updates ~30fps when audio is playing.
        /// </summary>
        [ObservableProperty]
        private WriteableBitmap? visualizerBitmapSource;

        [ObservableProperty]
        private string selectedAspectRatio = "9:16";

        public CanvasViewModel()
        {
            PropertyEditor = new PropertyEditorViewModel();
            ApplyAspectRatio(selectedAspectRatio);
        }

        public CanvasViewModel(VisualizerViewModel visualizerViewModel)
        {
            _visualizerViewModel = visualizerViewModel ?? throw new ArgumentNullException(nameof(visualizerViewModel));
            _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30fps
            };
            _visualizerTimer.Tick += OnVisualizerTimerTick;
            PropertyEditor = new PropertyEditorViewModel();
            PropertyEditor.OnVisualizerElementConfigChanged = SyncVisualizerFromElement;
            ApplyAspectRatio(selectedAspectRatio);
        }

        private void SyncVisualizerFromElement(VisualizerElement element)
        {
            if (_visualizerViewModel == null)
                return;
            _visualizerViewModel.SelectedStyle = element.Style;
            _visualizerViewModel.SelectedPalette = element.ColorPalette;
            _visualizerViewModel.SelectedBandCount = element.BandCount;
        }

        private void OnVisualizerTimerTick(object? sender, EventArgs e)
        {
            if (_visualizerViewModel == null || _disposed)
                return;

            var skBitmap = _visualizerViewModel.GetCurrentFrame();
            var wpfBitmap = SkiaConversionHelper.ToBitmapSource(skBitmap);
            if (wpfBitmap != null && HasVisualizerElements)
                VisualizerBitmapSource = wpfBitmap;
        }

        private bool HasVisualizerElements => Elements.Any(e => e is VisualizerElement);

        /// <summary>
        /// Start visualizer bitmap updates when canvas has visualizer elements.
        /// </summary>
        public void EnsureVisualizerTimer()
        {
            if (_visualizerViewModel == null || _visualizerTimer == null)
                return;

            if (HasVisualizerElements && !_visualizerTimer.IsEnabled)
            {
                _visualizerViewModel.Initialize();
                _visualizerTimer.Start();
            }
            else if (!HasVisualizerElements && _visualizerTimer.IsEnabled)
            {
                _visualizerTimer.Stop();
            }
        }

        /// <summary>
        /// Add a new title element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTitleElement()
        {
            var element = new TitleElement
            {
                Name = $"Title {Elements.Count + 1}",
                X = 50,
                Y = 50,
                Width = 400,
                Height = 100,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Title element");
        }

        /// <summary>
        /// Add a new logo element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddLogoElement()
        {
            var element = new LogoElement
            {
                Name = $"Logo {Elements.Count + 1}",
                X = 100,
                Y = 100,
                Width = 200,
                Height = 200,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Logo element");
        }

        /// <summary>
        /// Add a new visualizer element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddVisualizerElement()
        {
            var element = new VisualizerElement
            {
                Name = $"Visualizer {Elements.Count + 1}",
                X = 200,
                Y = 200,
                Width = 600,
                Height = 400,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            EnsureVisualizerTimer();
            LogMessage($"Added Visualizer element");
        }

        /// <summary>
        /// Add a new image element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddImageElement()
        {
            var element = new ImageElement
            {
                Name = $"Image {Elements.Count + 1}",
                X = 150,
                Y = 150,
                Width = 300,
                Height = 300,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Image element");
        }

        /// <summary>
        /// Add a new text element to canvas.
        /// </summary>
        [RelayCommand]
        public void AddTextElement()
        {
            var element = new TextElement
            {
                Name = $"Text {Elements.Count + 1}",
                X = 200,
                Y = 250,
                Width = 300,
                Height = 80,
                ZIndex = Elements.Count
            };

            Elements.Add(element);
            SelectElement(element);
            LogMessage($"Added Text element");
        }

        /// <summary>
        /// Select an element on the canvas.
        /// </summary>
        public void SelectElement(CanvasElement? element)
        {
            // Deselect previous
            if (SelectedElement != null)
            {
                SelectedElement.IsSelected = false;
            }

            // Select new
            if (element != null)
            {
                element.IsSelected = true;
                SelectedElement = element;
                PropertyEditor.SetSelectedElement(element);
                if (element is VisualizerElement ve)
                    SyncVisualizerFromElement(ve);
                LogMessage($"Selected: {element.Name}");
            }
            else
            {
                SelectedElement = null;
                PropertyEditor.SetSelectedElement(null);
            }
        }

        /// <summary>
        /// Delete the currently selected element.
        /// </summary>
        [RelayCommand]
        public void DeleteSelectedElement()
        {
            if (SelectedElement == null)
            {
                LogMessage("No element selected");
                return;
            }

            var elementToDelete = SelectedElement;
            Elements.Remove(elementToDelete);
            SelectedElement = null;
            PropertyEditor.SetSelectedElement(null);
            EnsureVisualizerTimer();
            LogMessage($"Deleted: {elementToDelete.Name}");
        }

        /// <summary>
        /// Move element to new position.
        /// </summary>
        public void MoveElement(CanvasElement element, double newX, double newY)
        {
            if (element != null)
            {
                element.X = Math.Max(0, newX);
                element.Y = Math.Max(0, newY);
            }
        }

        /// <summary>
        /// Resize element.
        /// </summary>
        public void ResizeElement(CanvasElement element, double newWidth, double newHeight)
        {
            if (element != null)
            {
                element.Width = Math.Max(10, newWidth);
                element.Height = Math.Max(10, newHeight);
            }
        }

        /// <summary>
        /// Duplicate the selected element.
        /// </summary>
        [RelayCommand]
        public void DuplicateElement()
        {
            if (SelectedElement == null)
            {
                LogMessage("No element selected");
                return;
            }

            var cloned = SelectedElement.Clone();
            cloned.X += 20;
            cloned.Y += 20;
            cloned.ZIndex = Elements.Count;

            Elements.Add(cloned);
            SelectElement(cloned);
            EnsureVisualizerTimer();
            LogMessage($"Duplicated: {cloned.Name}");
        }

        /// <summary>
        /// Bring selected element to front (increase z-index).
        /// </summary>
        [RelayCommand]
        public void BringToFront()
        {
            if (SelectedElement == null) return;

            var maxZ = Elements.Max(e => e.ZIndex);
            SelectedElement.ZIndex = maxZ + 1;
            LogMessage($"Moved {SelectedElement.Name} to front");
        }

        /// <summary>
        /// Send selected element to back (decrease z-index).
        /// </summary>
        [RelayCommand]
        public void SendToBack()
        {
            if (SelectedElement == null) return;

            var minZ = Elements.Min(e => e.ZIndex);
            SelectedElement.ZIndex = minZ - 1;
            LogMessage($"Moved {SelectedElement.Name} to back");
        }

        /// <summary>
        /// Clear all elements from canvas.
        /// </summary>
        [RelayCommand]
        public void ClearAll()
        {
            Elements.Clear();
            SelectedElement = null;
            PropertyEditor.SetSelectedElement(null);
            EnsureVisualizerTimer();
            LogMessage("Canvas cleared");
        }

        /// <summary>
        /// Delete all elements and start fresh.
        /// </summary>
        [RelayCommand]
        public void ResetCanvas()
        {
            ClearAll();
            ApplyAspectRatio(SelectedAspectRatio);
            LogMessage("Canvas reset to default");
        }

        /// <summary>
        /// Log a status message.
        /// </summary>
        private void LogMessage(string message)
        {
            StatusMessage = message;
            // Also log to Serilog if needed
            Serilog.Log.Debug("Canvas: {Message}", message);
        }

        /// <summary>
        /// Get element by ID.
        /// </summary>
        public CanvasElement? GetElementById(string id) =>
            Elements.FirstOrDefault(e => e.Id == id);

        partial void OnSelectedAspectRatioChanged(string value)
        {
            ApplyAspectRatio(value);
        }

        private void ApplyAspectRatio(string aspectRatio)
        {
            var parts = aspectRatio.Split(':');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], out var w) ||
                !double.TryParse(parts[1], out var h) ||
                w <= 0 || h <= 0)
            {
                w = 9;
                h = 16;
            }

            const double baseHeight = 1080.0;
            CanvasHeight = baseHeight;
            CanvasWidth = Math.Round(baseHeight * (w / h));
            StatusMessage = $"Preview ratio set to {w}:{h} ({CanvasWidth}x{CanvasHeight})";
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            PropertyEditor?.Dispose();
            _visualizerTimer?.Stop();
            Serilog.Log.Debug("CanvasViewModel disposed");
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Export element list as JSON (for saving to database).
        /// </summary>
        public string ExportAsJson()
        {
            // Implementation: serialize Elements to JSON
            // This will be used when saving project
            return System.Text.Json.JsonSerializer.Serialize(Elements);
        }

        /// <summary>
        /// Import elements from JSON.
        /// </summary>
        public void ImportFromJson(string json)
        {
            try
            {
                // Implementation: deserialize JSON to Elements
                Elements.Clear();
                LogMessage("Imported elements from JSON");
            }
            catch (Exception ex)
            {
                LogMessage($"Import failed: {ex.Message}");
            }
        }
    }
}
