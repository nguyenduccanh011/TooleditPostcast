#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.Compositing;
using PodcastVideoEditor.Ui.Controls;
using Serilog;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Beta preview window that drives the single-pass Skia compositor with the current project's
/// visual layers. Prefers the GPU surface (GLWpfControl + GRContext) and transparently falls back
/// to the raster surface if GL initialisation fails. Built in code-behind (no XAML) to keep the
/// integration surface minimal while the GPU compositor matures.
/// </summary>
public sealed class CompositorPreviewWindow : Window
{
    private readonly Border _previewHost = new();
    private readonly Slider _scrub;
    private readonly Button _playButton;
    private readonly TextBlock _timeText;
    private readonly TextBlock _backendText;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    private readonly IReadOnlyList<RenderVisualSegment> _segments;
    private readonly ICompositorVisualizerSource? _visualizers;
    private readonly int _outW;
    private readonly int _outH;
    private readonly int _fps;
    private readonly double _duration;

    private ICompositorPreviewSurface _surface = null!;
    private double _baseSeconds;
    private bool _playing;
    private bool _suppressScrub;

    public CompositorPreviewWindow(IReadOnlyList<RenderVisualSegment> segments, int outputWidth, int outputHeight, int frameRate,
        ICompositorVisualizerSource? visualizerSource = null)
    {
        _segments = segments ?? Array.Empty<RenderVisualSegment>();
        _visualizers = visualizerSource;
        _outW = outputWidth;
        _outH = outputHeight;
        _fps = frameRate;
        _duration = _segments.Count > 0 ? Math.Max(1.0, _segments.Max(s => s.EndTime)) : 1.0;

        Title = "GPU Compositor Preview (Beta)";
        Width = 540;
        Height = 980;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_previewHost, 0);
        root.Children.Add(_previewHost);

        // Prefer the GPU surface; fall back to raster on GL failure.
        _previewHost.Child = BuildGpuSurface();

        // ── Transport bar ────────────────────────────────────────────────
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 10) };
        Grid.SetRow(bar, 1);

        _playButton = new Button { Content = "▶ Play", Width = 84, Padding = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        _playButton.Click += (_, _) => TogglePlay();
        bar.Children.Add(_playButton);

        _scrub = new Slider
        {
            Minimum = 0,
            Maximum = _duration,
            Width = 300,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };
        _scrub.ValueChanged += OnScrubChanged;
        bar.Children.Add(_scrub);

        _timeText = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, MinWidth = 92, Text = FormatTime(0) };
        bar.Children.Add(_timeText);

        _backendText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0xd0, 0x8a)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "GPU",
        };
        bar.Children.Add(_backendText);

        root.Children.Add(bar);
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;

        // Watchdog: if the GPU surface produces no frame within 2s (GL init silently failed),
        // fall back to the raster compositor so the user always sees content.
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            if (_surface is GpuCompositorPreviewControl gpu && !gpu.GlReady)
            {
                Log.Information("GPU preview watchdog: no GL frame after 2s — falling back to raster");
                FallBackToRaster();
            }
        };
        watchdog.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _surface.IsPlaying = false;
            (_visualizers as IDisposable)?.Dispose();
        };
    }

    private UIElement BuildGpuSurface()
    {
        var gpu = new GpuCompositorPreviewControl
        {
            Segments = _segments,
            OutputWidth = _outW,
            OutputHeight = _outH,
            FrameRate = _fps,
            PlayheadSeconds = 0,
            Visualizers = _visualizers,
        };
        gpu.GlFailed += OnGpuFailed;
        _surface = gpu;
        return gpu;
    }

    private UIElement BuildRasterSurface(double playhead)
    {
        var raster = new CompositorPreviewControl
        {
            Segments = _segments,
            OutputWidth = _outW,
            OutputHeight = _outH,
            FrameRate = _fps,
            PlayheadSeconds = playhead,
            IsPlaying = _playing,
            Visualizers = _visualizers,
        };
        _surface = raster;
        return raster;
    }

    private void OnGpuFailed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(FallBackToRaster);

    private void FallBackToRaster()
    {
        if (_surface is not GpuCompositorPreviewControl)
            return; // already fell back

        var t = _surface.PlayheadSeconds;
        _previewHost.Child = BuildRasterSurface(t);
        _backendText.Text = "CPU (raster)";
        _backendText.Foreground = new SolidColorBrush(Color.FromRgb(0xd0, 0xb0, 0x6a));
        Log.Information("GPU preview: fell back to raster compositor.");
    }

    private void TogglePlay()
    {
        if (_playing)
        {
            _baseSeconds = _surface.PlayheadSeconds;
            _clock.Stop();
            _timer.Stop();
            _surface.IsPlaying = false;
            _playing = false;
            _playButton.Content = "▶ Play";
        }
        else
        {
            _baseSeconds = _surface.PlayheadSeconds >= _duration ? 0 : _surface.PlayheadSeconds;
            _clock.Restart();
            _timer.Start();
            _surface.IsPlaying = true;
            _playing = true;
            _playButton.Content = "⏸ Pause";
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_playing)
            return;

        var t = _baseSeconds + _clock.Elapsed.TotalSeconds;
        if (t >= _duration)
        {
            _baseSeconds = 0;
            _clock.Restart();
            t = 0;
        }

        _surface.PlayheadSeconds = t;
        _suppressScrub = true;
        _scrub.Value = Math.Min(_duration, t);
        _suppressScrub = false;
        _timeText.Text = FormatTime(t);
    }

    private void OnScrubChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressScrub)
            return;

        _surface.PlayheadSeconds = e.NewValue;
        _baseSeconds = e.NewValue;
        if (_playing)
            _clock.Restart();
        _timeText.Text = FormatTime(e.NewValue);
    }

    private string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{t:mm\\:ss\\.ff} / {TimeSpan.FromSeconds(_duration):mm\\:ss}";
    }
}
