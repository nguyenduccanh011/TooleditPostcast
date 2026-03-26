using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PodcastVideoEditor.Ui.Controls;

/// <summary>
/// Commercial-grade HSV color picker with spectrum canvas, hue slider,
/// hex input, preset swatches, and recent color history.
/// </summary>
public partial class ColorPickerPopup : UserControl
{
    // ── Dependency Properties ──────────────────────────────────────────
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(nameof(SelectedColor), typeof(string), typeof(ColorPickerPopup),
            new FrameworkPropertyMetadata("#FF0000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedColorChanged));

    public string SelectedColor
    {
        get => (string)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>Raised every time SelectedColor changes (real-time while dragging).</summary>
    public event Action<string>? ColorChanged;

    // ── HSV state (0-360, 0-1, 0-1) ───────────────────────────────────
    private double _hue = 0;
    private double _saturation = 1;
    private double _valueBrightness = 1;

    private bool _isDraggingSv;
    private bool _isDraggingHue;
    private bool _suppressSync; // prevent re-entrant colour updates

    // ── Presets ─────────────────────────────────────────────────────────
    private static readonly string[] PresetColors = new[]
    {
        "#FFFFFF", "#C0C0C0", "#808080", "#000000",
        "#FF0000", "#FF4500", "#FF8C00", "#FFD700",
        "#FFFF00", "#7FFF00", "#00FF00", "#00CED1",
        "#00BFFF", "#0000FF", "#8A2BE2", "#FF00FF",
        "#FF69B4", "#FF1493", "#800000", "#008080"
    };

    private static readonly List<string> RecentColors = new(8);

    // ── Constructor ─────────────────────────────────────────────────────
    public ColorPickerPopup()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildSwatches(PresetPanel, PresetColors);
        BuildSwatches(RecentPanel, RecentColors.ToArray());
        SyncFromHex(SelectedColor ?? "#FF0000");
        UpdateCanvasSize();
    }

    // ── Canvas sizing ───────────────────────────────────────────────────
    private void UpdateCanvasSize()
    {
        if (SvCanvas.ActualWidth < 1)
        {
            SvCanvas.SizeChanged += SvCanvas_SizeChanged;
            return;
        }
        ApplyCanvasSize();
    }

    private void SvCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SvCanvas.SizeChanged -= SvCanvas_SizeChanged;
        ApplyCanvasSize();
    }

    private void ApplyCanvasSize()
    {
        var w = SvCanvas.ActualWidth;
        var h = SvCanvas.ActualHeight;
        WhiteGradientRect.Width = w;
        WhiteGradientRect.Height = h;
        BlackGradientRect.Width = w;
        BlackGradientRect.Height = h;

        HueSelector.Width = HueCanvas.ActualWidth;
        UpdateSvSelector();
        UpdateHueSelector();
    }

    // ────────────────────────────────────────────────────────────────────
    //  SV Canvas (Saturation-Value)
    // ────────────────────────────────────────────────────────────────────
    private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSv = true;
        SvCanvas.CaptureMouse();
        PickSvFromMouse(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingSv)
            PickSvFromMouse(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSv = false;
        SvCanvas.ReleaseMouseCapture();
    }

    private void PickSvFromMouse(Point pos)
    {
        var w = SvCanvas.ActualWidth;
        var h = SvCanvas.ActualHeight;
        if (w < 1 || h < 1) return;

        _saturation = Math.Clamp(pos.X / w, 0, 1);
        _valueBrightness = Math.Clamp(1 - pos.Y / h, 0, 1);
        UpdateSvSelector();
        ApplyHsvToColor();
    }

    private void UpdateSvSelector()
    {
        var w = SvCanvas.ActualWidth;
        var h = SvCanvas.ActualHeight;
        if (w < 1 || h < 1) return;

        Canvas.SetLeft(SvSelector, _saturation * w - 7);
        Canvas.SetTop(SvSelector, (1 - _valueBrightness) * h - 7);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hue Slider
    // ────────────────────────────────────────────────────────────────────
    private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingHue = true;
        HueCanvas.CaptureMouse();
        PickHueFromMouse(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingHue)
            PickHueFromMouse(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingHue = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void PickHueFromMouse(Point pos)
    {
        var h = HueCanvas.ActualHeight;
        if (h < 1) return;

        _hue = Math.Clamp(pos.Y / h, 0, 1) * 360;
        UpdateHueBackground();
        UpdateHueSelector();
        ApplyHsvToColor();
    }

    private void UpdateHueSelector()
    {
        var h = HueCanvas.ActualHeight;
        if (h < 1) return;

        Canvas.SetTop(HueSelector, (_hue / 360.0) * h - 2);
    }

    private void UpdateHueBackground()
    {
        // Update SV canvas base color to pure hue
        var pureColor = HsvToColor(_hue, 1, 1);
        SvCanvas.Background = new SolidColorBrush(pureColor);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Color Conversion & Application
    // ────────────────────────────────────────────────────────────────────
    private void ApplyHsvToColor()
    {
        if (_suppressSync) return;
        _suppressSync = true;

        var color = HsvToColor(_hue, _saturation, _valueBrightness);
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        HexInput.Text = hex;
        NewColorPreview.Background = new SolidColorBrush(color);
        SelectedColor = hex;
        ColorChanged?.Invoke(hex);

        _suppressSync = false;
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerPopup picker && !picker._suppressSync)
        {
            picker.SyncFromHex(e.NewValue as string ?? "#FF0000");
        }
    }

    private void SyncFromHex(string hex)
    {
        _suppressSync = true;
        try
        {
            var color = ParseHexColor(hex);
            ColorToHsv(color, out _hue, out _saturation, out _valueBrightness);

            UpdateHueBackground();
            UpdateHueSelector();
            UpdateSvSelector();

            HexInput.Text = hex;
            NewColorPreview.Background = new SolidColorBrush(color);
        }
        catch { /* ignore invalid hex during sync */ }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// Set the "old" color that appears as a comparison reference.
    /// Call this when opening the picker popup.
    /// </summary>
    public void SetOldColor(string hex)
    {
        try
        {
            OldColorPreview.Background = new SolidColorBrush(ParseHexColor(hex));
        }
        catch
        {
            OldColorPreview.Background = Brushes.Transparent;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hex Input
    // ────────────────────────────────────────────────────────────────────
    private void HexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitHexInput();
    }

    private void HexInput_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitHexInput();
    }

    private void CommitHexInput()
    {
        var text = HexInput.Text?.Trim() ?? "";
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 7)
        {
            SyncFromHex(text);
            SelectedColor = text;
            ColorChanged?.Invoke(text);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Swatches
    // ────────────────────────────────────────────────────────────────────
    private void BuildSwatches(WrapPanel panel, string[] colors)
    {
        panel.Children.Clear();
        foreach (var hex in colors)
        {
            var border = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x54, 0x6e, 0x7a)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Background = new SolidColorBrush(ParseHexColor(hex))
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                SyncFromHex(hex);
                SelectedColor = hex;
                ColorChanged?.Invoke(hex);
            };
            panel.Children.Add(border);
        }
    }

    /// <summary>
    /// Adds a colour to the recent colours list (front, deduped, max 8).
    /// </summary>
    public static void AddRecentColor(string hex)
    {
        RecentColors.Remove(hex);
        RecentColors.Insert(0, hex);
        if (RecentColors.Count > 8) RecentColors.RemoveAt(8);
    }

    /// <summary>Call after a colour is committed to update the Recent panel.</summary>
    public void RefreshRecent()
    {
        BuildSwatches(RecentPanel, RecentColors.ToArray());
    }

    // ────────────────────────────────────────────────────────────────────
    //  Color Math (HSV ↔ RGB)
    // ────────────────────────────────────────────────────────────────────
    private static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;

        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r1 + m) * 255, 0, 255),
            (byte)Math.Clamp((g1 + m) * 255, 0, 255),
            (byte)Math.Clamp((b1 + m) * 255, 0, 255));
    }

    private static void ColorToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            h = 60 * (((r - g) / delta) + 4);
        }

        if (h < 0) h += 360;
    }

    private static Color ParseHexColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.White;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return c;
        }
        catch
        {
            return Colors.White;
        }
    }
}
