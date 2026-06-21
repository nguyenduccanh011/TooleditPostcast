#nullable enable
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Small progress window for the beta compositor export. Shows percent + a Cancel button.
/// Built in code-behind to keep the integration surface minimal.
/// </summary>
public sealed class CompositorExportProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _label;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public CompositorExportProgressWindow()
    {
        Title = "Compositor Export (Beta)";
        Width = 440;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x24));

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _label = new TextBlock
        {
            Text = "Đang render khung hình…",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(_label, 0);
        grid.Children.Add(_label);

        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 22 };
        Grid.SetRow(_bar, 1);
        grid.Children.Add(_bar);

        _cancelButton = new Button
        {
            Content = "Hủy",
            Width = 90,
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _cancelButton.Click += (_, _) =>
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            _cancelButton.IsEnabled = false;
            _label.Text = "Đang hủy…";
        };
        Grid.SetRow(_cancelButton, 2);
        grid.Children.Add(_cancelButton);

        Content = grid;
        Closed += (_, _) => { try { _cts.Cancel(); } catch { /* ignore */ } };
    }

    public void Report(double progress)
    {
        var pct = Math.Clamp(progress * 100.0, 0, 100);
        _bar.Value = pct;
        _label.Text = $"Đang render khung hình… {(int)pct}%";
    }
}
