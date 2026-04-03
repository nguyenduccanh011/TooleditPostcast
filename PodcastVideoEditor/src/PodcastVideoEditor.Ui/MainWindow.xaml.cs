#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Services;
using PodcastVideoEditor.Ui.Services.Update;
using PodcastVideoEditor.Ui.ViewModels;
using PodcastVideoEditor.Ui.Views;
using Serilog;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PodcastVideoEditor.Ui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProjectViewModel _projectViewModel;
    private readonly AudioPlayerViewModel _audioPlayerViewModel;
    private readonly TimelineViewModel _timelineViewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly AutosaveService _autosaveService;
    private readonly IUpdateService _updateService;
    private readonly IAppInfoService _appInfoService;
    private readonly string _appDataPath;
    private bool _initialLoadDone;
    private bool _isClosing;
    private bool _isTabSwitching;

    /// <summary>
    /// Constructor receives all dependencies from the DI container (composition root in App.xaml.cs).
    /// No object construction or wiring happens here — only event subscriptions specific to this window.
    /// </summary>
    public MainWindow(
        MainViewModel mainViewModel,
        ProjectViewModel projectViewModel,
        AudioPlayerViewModel audioPlayerViewModel,
        TimelineViewModel timelineViewModel,
        SettingsViewModel settingsViewModel,
        AutosaveService autosaveService,
        IAIAnalysisOrchestrator aiOrchestrator,
        IUpdateService updateService,
        IAppInfoService appInfoService)
    {
        try
        {
            Log.Information("MainWindow constructor starting");
            InitializeComponent();

            _appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor");

            _mainViewModel = mainViewModel;
            _projectViewModel = projectViewModel;
            _audioPlayerViewModel = audioPlayerViewModel;
            _timelineViewModel = timelineViewModel;
            _settingsViewModel = settingsViewModel;
            _autosaveService = autosaveService;
            _updateService = updateService;
            _appInfoService = appInfoService;

            // Wire up AI orchestrator for script analysis
            _timelineViewModel.SetOrchestrator(aiOrchestrator);

            DataContext = _mainViewModel;
            SettingsPanel.DataContext = _settingsViewModel;

            _audioPlayerViewModel.AudioLoaded += OnAudioLoaded;

            _timelineViewModel.PropertyChanged += OnTimelinePropertyChanged;
            _timelineViewModel.Tracks.CollectionChanged += OnTimelineTracksChanged;

            Log.Information("MainWindow initialized");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FATAL ERROR in MainWindow constructor: {Message}", ex.Message);
            MessageBox.Show($"Fatal Error:\n{ex.GetType().Name}: {ex.Message}", "Startup Error");
            throw;
        }
    }

    // ── Named event handlers (replaces lambdas — required for clean unsubscription) ──

    private void OnTimelinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_projectViewModel.CurrentProject != null
            && !_timelineViewModel.IsLoadingFromProject
            && !_timelineViewModel.IsDeferringThumbnailUpdate
            && e.PropertyName is not (nameof(TimelineViewModel.PlayheadPosition)
                or nameof(TimelineViewModel.IsPlaying)
                or nameof(TimelineViewModel.StatusMessage)))
        {
            _autosaveService.RequestSave();
        }
    }

    private void OnTimelineTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_projectViewModel.CurrentProject != null && !_timelineViewModel.IsLoadingFromProject)
            _autosaveService.RequestSave();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Force the initial tab selection so the Home content renders immediately
        // (WPF TabControl with a stripped-down ControlTemplate may not display
        // the first tab's content until a SelectionChanged event fires).
        MainTabControl.SelectedIndex = 0;

        AppDataPathText.Text = _appDataPath;
        LogPathText.Text = Path.Combine(_appDataPath, "Logs");
        PopulateBuildInfo();
        await LoadProjectsSafeAsync();
        await InitializeFfmpegStatusAsync();
        await CheckForUpdatesOnStartupAsync();
        _initialLoadDone = true;
    }

    private void PopulateBuildInfo()
    {
        AboutProductText.Text = _appInfoService.ProductName;
        AboutVersionText.Text = $"Version {_appInfoService.DisplayVersion}";
        InstalledVersionText.Text = $"Installed version: {_appInfoService.DisplayVersion}";
        InstallDirectoryText.Text = $"Install directory: {_appInfoService.InstallDirectory}";
        BundledFfmpegText.Text = _appInfoService.BundledFfmpegPath is null
            ? "Bundled FFmpeg: not found in this build."
            : $"Bundled FFmpeg: {_appInfoService.BundledFfmpegPath}";
        UpdateStatusText.Text = "Checks GitHub Releases for the latest stable installer.";
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_updateService.ShouldCheckForUpdates())
            return;

        var result = await _updateService.CheckForUpdatesAsync(ignoreSchedule: false);
        if (result.IsUpdateAvailable)
        {
            UpdateStatusText.Text = result.Message;
            ShowUpdateResult(result, isInteractive: false);
            return;
        }

        if (!result.WasSkipped)
            UpdateStatusText.Text = result.Message;

        if (!result.IsSuccessful && !result.WasSkipped)
            Log.Warning("Startup update check failed: {Message}", result.Message);
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        UpdateStatusText.Text = "Checking GitHub Releases...";
        var result = await _updateService.CheckForUpdatesAsync(ignoreSchedule: true);
        UpdateStatusText.Text = result.Message;
        ShowUpdateResult(result, isInteractive: true);
    }

    private void ShowUpdateResult(UpdateCheckResult result, bool isInteractive)
    {
        if (result.IsUpdateAvailable)
        {
            var latestVersion = result.LatestVersion is null
                ? "new version"
                : VersionParser.ToDisplayString(result.LatestVersion);
            var downloadUrl = result.DownloadUrl ?? result.ReleasePageUrl;
            var message =
                $"{result.ReleaseTitle ?? "A new release"} is available.{Environment.NewLine}{Environment.NewLine}" +
                $"Current version: {_appInfoService.DisplayVersion}{Environment.NewLine}" +
                $"Latest version: {latestVersion}{Environment.NewLine}{Environment.NewLine}" +
                "Open the download page now?";

            if (MessageBox.Show(message, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes
                && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                OpenExternalUrl(downloadUrl);
            }

            return;
        }

        if (isInteractive)
        {
            var icon = result.IsSuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning;
            MessageBox.Show(result.Message, "Check for Updates", MessageBoxButton.OK, icon);
        }
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Global keyboard shortcuts. Ctrl+Z/Y for undo/redo, Space for play/pause,
    /// Delete for segment removal, Ctrl+S to save, J/K/L for shuttle control.
    /// Shortcuts that produce text (Space, Delete) are suppressed when a TextBox has focus.
    /// </summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        // ── Ctrl combos (safe even when TextBox focused) ──
        if (ctrl && !shift && e.Key == System.Windows.Input.Key.Z)
        {
            _mainViewModel.UndoRedoService.Undo();
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == System.Windows.Input.Key.Y || (shift && e.Key == System.Windows.Input.Key.Z)))
        {
            _mainViewModel.UndoRedoService.Redo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == System.Windows.Input.Key.S)
        {
            _ = _projectViewModel.SaveProjectAsync();
            e.Handled = true;
            return;
        }

        // ── Non-modifier keys: skip when user is typing in a TextBox/PasswordBox ──
        var focused = System.Windows.Input.FocusManager.GetFocusedElement(this);
        if (focused is System.Windows.Controls.TextBox or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox)
            return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Space:
                _timelineViewModel?.ResetScrubState();
                _audioPlayerViewModel.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Delete:
                // Canvas element takes priority: if one is selected, delete it (with its segment).
                // Fall back to timeline-only delete when nothing is selected on canvas.
                if (_mainViewModel.CanvasViewModel.SelectedElement != null)
                    _mainViewModel.CanvasViewModel.DeleteSelectedElementCommand.Execute(null);
                else if (_timelineViewModel.DeleteSelectedSegmentCommand.CanExecute(null))
                    _timelineViewModel.DeleteSelectedSegmentCommand.Execute(null);
                e.Handled = true;
                break;

            // J/K/L shuttle control (Premiere Pro style)
            case System.Windows.Input.Key.K:
                // K = pause
                if (_audioPlayerViewModel.IsPlaying)
                    _audioPlayerViewModel.PauseCommand.Execute(null);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.L:
                // L = play forward (if paused, start playing)
                if (!_audioPlayerViewModel.IsPlaying)
                    _audioPlayerViewModel.PlayCommand.Execute(null);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.J:
                // J = rewind 5 seconds
                _timelineViewModel.SeekTo(Math.Max(0, _timelineViewModel.PlayheadPosition - 5));
                e.Handled = true;
                break;

            // Comma/Period = nudge playhead ±0.1s (frame-stepping)
            case System.Windows.Input.Key.OemComma:
                _timelineViewModel.SeekTo(Math.Max(0, _timelineViewModel.PlayheadPosition - 0.1));
                e.Handled = true;
                break;

            case System.Windows.Input.Key.OemPeriod:
                _timelineViewModel.SeekTo(Math.Min(_timelineViewModel.TotalDuration, _timelineViewModel.PlayheadPosition + 0.1));
                e.Handled = true;
                break;

            // Home/End = jump to start/end
            case System.Windows.Input.Key.Home:
                _timelineViewModel.SeekTo(0);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.End:
                _timelineViewModel.SeekTo(_timelineViewModel.TotalDuration);
                e.Handled = true;
                break;
        }
    }

    private async void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Sync title bar tab buttons with current tab
        switch (MainTabControl.SelectedIndex)
        {
            case 0: TabBtnHome.IsChecked = true; break;
            case 1: TabBtnEditor.IsChecked = true; break;
            case 2: TabBtnSettings.IsChecked = true; break;
        }

        // Show render button only on Editor tab
        TitleBarRenderBtn.Visibility = MainTabControl.SelectedIndex == 1
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        if (!_initialLoadDone || _isTabSwitching)
            return;

        _isTabSwitching = true;
        try
        {
            // Flush pending autosave when leaving the Editor tab so changes
            // (e.g. property edits) are persisted before navigating away.
            // Only flushes when there's actually a pending debounced save.
            if (MainTabControl.SelectedIndex != 1 && _autosaveService.HasPendingSave)
                await _autosaveService.FlushAsync();

            if (MainTabControl.SelectedIndex == 0)
                await LoadProjectsSafeAsync();
        }
        finally
        {
            _isTabSwitching = false;
        }
    }

    // ── Title bar button handlers ──

    private void TabButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb
            && int.TryParse(rb.Tag?.ToString(), out int index))
        {
            MainTabControl.SelectedIndex = index;
        }
    }

    private void OpenRenderDialog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Views.RenderDialog
        {
            Owner = this,
            DataContext = _mainViewModel.RenderViewModel,
            Project = _projectViewModel.CurrentProject
        };
        dialog.ShowDialog();
    }

    private void MinimizeButton_Click(object sender, System.Windows.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, System.Windows.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
        => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Compensate for WPF maximized overshoot with WindowStyle=None
        if (WindowState == WindowState.Maximized)
            WindowBorder.Padding = new Thickness(7);
        else
            WindowBorder.Padding = new Thickness(0);

        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private async Task LoadProjectsSafeAsync()
    {
        try
        {
            await _projectViewModel.LoadProjectsAsync();
            Log.Information("Projects loaded: {Count}", _projectViewModel.Projects.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading projects on startup");
        }
    }

    private async Task InitializeFfmpegStatusAsync()
    {
        var path = FFmpegService.GetFFmpegPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _settingsViewModel.FfmpegPath = path;
            FFmpegStatusText.Text = $"FFmpeg ready: {path}";
            return;
        }

        var result = await FFmpegService.InitializeAsync(_settingsViewModel.FfmpegPath);
        if (result.IsValid)
        {
            _settingsViewModel.FfmpegPath = result.FFmpegPath ?? string.Empty;
            FFmpegStatusText.Text = result.Message;
        }
        else
        {
            FFmpegStatusText.Text = result.Message;
        }
    }

    private async void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _projectViewModel.NewProjectName = dialog.ProjectName;
            await _projectViewModel.CreateProjectAsync();
            await LoadProjectAudioAsync();
            MainTabControl.SelectedIndex = 1;
        }
    }

    private async void OpenSelectedMenu_Click(object sender, RoutedEventArgs e)
    {
        await OpenCurrentProjectAsync();
    }

    private async void ProjectsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await OpenCurrentProjectAsync();
    }

    /// <summary>Opens the currently selected project and switches to the Editor tab.</summary>
    private async Task OpenCurrentProjectAsync()
    {
        if (_projectViewModel.CurrentProject == null)
        {
            MessageBox.Show("Please select a project first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _projectViewModel.OpenProjectAsync(_projectViewModel.CurrentProject);
        await LoadProjectAudioAsync();
        MainTabControl.SelectedIndex = 1;
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 2;
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"{_appInfoService.ProductName}{Environment.NewLine}" +
            $"Version {_appInfoService.DisplayVersion}{Environment.NewLine}" +
            $"Install directory: {_appInfoService.InstallDirectory}",
            "About");
    }

    private async void CheckForUpdatesMenu_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInteractiveAsync();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInteractiveAsync();
    }

    private void BrowseFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            _settingsViewModel.FfmpegPath = dialog.FileName;
        }
    }

    private async void ValidateFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        FFmpegStatusText.Text = "Validating FFmpeg...";
        var result = await FFmpegService.InitializeAsync(_settingsViewModel.FfmpegPath);
        if (result.IsValid && !string.IsNullOrWhiteSpace(result.FFmpegPath))
            _settingsViewModel.FfmpegPath = result.FFmpegPath;
        FFmpegStatusText.Text = result.Message;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var logsPath = Path.Combine(_appDataPath, "Logs");
        Directory.CreateDirectory(logsPath);
        System.Diagnostics.Process.Start("explorer.exe", logsPath);
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Settings",
            Filter = "JSON Files (*.json)|*.json",
            FileName = "podcast_editor_settings.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
            _settingsViewModel.ExportSettings(dialog.FileName);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Settings",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        var confirm = System.Windows.MessageBox.Show(
            "Importing will overwrite ALL current settings (API keys, models, fallback chain, paths).\n\nContinue?",
            "Confirm Import",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
            _settingsViewModel.ImportSettings(dialog.FileName);
    }

    private async void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        // Guard: if we're already in the closing sequence (after flush), let it close.
        if (_isClosing)
            return;

        // Cancel the close, perform async cleanup, then close again.
        e.Cancel = true;
        _isClosing = true;

        _audioPlayerViewModel.AudioLoaded -= OnAudioLoaded;
        _timelineViewModel.PropertyChanged -= OnTimelinePropertyChanged;
        _timelineViewModel.Tracks.CollectionChanged -= OnTimelineTracksChanged;

        // Flush any pending autosave before disposing
        await _autosaveService.FlushAsync(force: true);
        _autosaveService.Dispose();

        _mainViewModel?.Dispose();

        // Now actually close the window
        Close();
    }

    private async Task LoadProjectAudioAsync()
    {
        var project = _projectViewModel.CurrentProject;
        if (project == null)
        {
            _audioPlayerViewModel.StopCommand.Execute(null);
            return;
        }

        _audioPlayerViewModel.StopCommand.Execute(null);

        if (!string.IsNullOrWhiteSpace(project.AudioPath) && System.IO.File.Exists(project.AudioPath))
        {
            await _audioPlayerViewModel.LoadAudioAsync(project.AudioPath);
            // Timeline + waveform sync is done in OnAudioLoaded
        }
        else
        {
            // No main audio file — compute duration from segments on the timeline
            _timelineViewModel.RecalculateDurationAndZoomToFit();
        }
    }

    /// <summary>
    /// Called whenever audio is loaded (Open project or Select audio). Syncs timeline and waveform.
    /// On first load (project open) we zoom-to-fit; on subsequent loads (e.g. replacing audio)
    /// we preserve the user's current zoom level and only update the duration.
    /// </summary>
    private void OnAudioLoaded(object? sender, EventArgs e)
    {
        _mainViewModel.VisualizerViewModel.VisualizerWidth = 800;
        _mainViewModel.VisualizerViewModel.VisualizerHeight = 300;
        _mainViewModel.VisualizerViewModel.Initialize();

        var audioDuration = Math.Max(1, _audioPlayerViewModel.TotalDuration);
        // Use whichever is longer: audio file or segment content
        var segmentEnd = _timelineViewModel.ComputeMaxSegmentEndTime();
        var durationSeconds = Math.Max(audioDuration, segmentEnd);
        _timelineViewModel.TotalDuration = durationSeconds;
        // PPS automatically recalculated via OnTotalDurationChanged —
        // do NOT reset TimelineWidth here so the user's zoom level is preserved.
    }

}
