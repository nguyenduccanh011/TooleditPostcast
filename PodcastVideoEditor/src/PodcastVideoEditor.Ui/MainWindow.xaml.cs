#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.ViewModels;
using PodcastVideoEditor.Ui.Views;
using Serilog;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PodcastVideoEditor.Ui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppDbContext _dbContext;
    private readonly ProjectViewModel _projectViewModel;
    private readonly RenderViewModel _renderViewModel;
    private readonly CanvasViewModel _canvasViewModel;
    private readonly AudioService _audioService;
    private readonly AudioPlayerViewModel _audioPlayerViewModel;
    private readonly VisualizerViewModel _visualizerViewModel;
    private readonly TimelineViewModel _timelineViewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly string _appDataPath;
    private bool _initialLoadDone;

    public MainWindow()
    {
        try
        {
            Log.Information("MainWindow constructor starting");
            InitializeComponent();

            _appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor");
            Directory.CreateDirectory(_appDataPath);

            var dbPath = Path.Combine(_appDataPath, "app.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            _dbContext = new AppDbContext(options);
            InitializeDatabase(_dbContext);

            var projectService = new ProjectService(_dbContext);
            _renderViewModel = new RenderViewModel();
            _audioService = new AudioService();
            _visualizerViewModel = new VisualizerViewModel(_audioService);
            _canvasViewModel = new CanvasViewModel(_visualizerViewModel);
            _projectViewModel = new ProjectViewModel(projectService, _canvasViewModel, _renderViewModel);
            _audioPlayerViewModel = new AudioPlayerViewModel(_audioService);
            _timelineViewModel = new TimelineViewModel(_audioService, _projectViewModel);

            _canvasViewModel.AttachProjectAndTimeline(_projectViewModel, _timelineViewModel);
            _renderViewModel.AttachTimeline(_timelineViewModel);
            _renderViewModel.AttachCanvas(_canvasViewModel);

            _mainViewModel = new MainViewModel(
                _projectViewModel,
                _renderViewModel,
                _canvasViewModel,
                _audioPlayerViewModel,
                _visualizerViewModel,
                _timelineViewModel);
            DataContext = _mainViewModel;
            Log.Information("MainWindow DataContext set, Projects count will load on Loaded");

            _audioPlayerViewModel.AudioLoaded += OnAudioLoaded;

            Log.Information("MainWindow initialized");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FATAL ERROR in MainWindow constructor: {Message}", ex.Message);
            MessageBox.Show($"Fatal Error:\n{ex.GetType().Name}: {ex.Message}", "Startup Error");
            throw;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppDataPathText.Text = _appDataPath;
        await LoadProjectsSafeAsync();
        await InitializeFfmpegStatusAsync();
        _initialLoadDone = true;
    }

    /// <summary>
    /// Global Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) undo/redo from anywhere in the window.
    /// </summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (ctrl && !shift && e.Key == System.Windows.Input.Key.Z)
        {
            _mainViewModel.UndoRedoService.Undo();
            e.Handled = true;
        }
        else if (ctrl && (e.Key == System.Windows.Input.Key.Y || (shift && e.Key == System.Windows.Input.Key.Z)))
        {
            _mainViewModel.UndoRedoService.Redo();
            e.Handled = true;
        }
    }

    private async void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Show render toolbar only when Editor tab (index 1) is active
        RenderToolbar.Visibility = MainTabControl.SelectedIndex == 1
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        if (_initialLoadDone && MainTabControl.SelectedIndex == 0)
            await LoadProjectsSafeAsync();
    }

    /// <summary>Generic handler that opens a button's ContextMenu as a dropdown.</summary>
    private void RenderTB_DropdownClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
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

    /// <summary>
    /// Initializes the database, applying all pending EF Core migrations.
    /// Handles the case where the schema was created out-of-band (e.g. EnsureCreated or
    /// a column was added directly), which causes Migrate() to fail with
    /// "duplicate column name". We detect such stale-history conditions and repair them
    /// before retrying the migration so that any genuinely missing columns (e.g. SegmentId)
    /// are added correctly.
    /// </summary>
    private static void InitializeDatabase(AppDbContext context)
    {
        try
        {
            context.Database.Migrate();
            Log.Information("Database migration applied successfully");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column name"))
        {
            Log.Warning(ex, "Migration conflict detected (column already exists in schema). Repairing migration history...");
            RepairMigrationHistory(context);
            // Retry – only genuinely missing migrations (like AddElementSegmentId) will now run
            context.Database.Migrate();
            Log.Information("Database migration applied successfully after history repair");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database migration failed, falling back to EnsureCreated");
            context.Database.EnsureCreated();
        }
    }

    /// <summary>
    /// Repairs the __EFMigrationsHistory table for migrations whose schema changes
    /// were already applied to the database (e.g. via EnsureCreated or a prior run)
    /// but whose history row is missing, causing subsequent Migrate() calls to conflict.
    /// </summary>
    private static void RepairMigrationHistory(AppDbContext context)
    {
        const string productVersion = "8.0.2";
        var conn = context.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // AddSegmentKind: adds Kind column to Segments
            MarkMigrationIfColumnExists(conn,
                migrationId: "20260210100000_AddSegmentKind",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Kind'",
                productVersion);

            // AddSegmentAudioProperties: adds Volume/FadeInDuration/FadeOutDuration to Segments
            MarkMigrationIfColumnExists(conn,
                migrationId: "20260313100000_AddSegmentAudioProperties",
                checkSql: "SELECT COUNT(*) FROM pragma_table_info('Segments') WHERE name='Volume'",
                productVersion);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

    private static void MarkMigrationIfColumnExists(
        System.Data.Common.DbConnection conn,
        string migrationId,
        string checkSql,
        string productVersion)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = checkSql;
        var count = (long)(checkCmd.ExecuteScalar() ?? 0L);
        if (count > 0)
        {
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText =
                "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, @ver)";
            var pId = insertCmd.CreateParameter(); pId.ParameterName = "@id"; pId.Value = migrationId;
            var pVer = insertCmd.CreateParameter(); pVer.ParameterName = "@ver"; pVer.Value = productVersion;
            insertCmd.Parameters.Add(pId);
            insertCmd.Parameters.Add(pVer);
            insertCmd.ExecuteNonQuery();
            Log.Information("Migration history repaired: marked {MigrationId} as applied", migrationId);
        }
    }

    private async Task InitializeFfmpegStatusAsync()
    {
        var path = FFmpegService.GetFFmpegPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            FFmpegPathTextBox.Text = path;
            FFmpegStatusText.Text = $"FFmpeg ready: {path}";
            return;
        }

        var result = await FFmpegService.InitializeAsync();
        if (result.IsValid)
        {
            FFmpegPathTextBox.Text = result.FFmpegPath ?? string.Empty;
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
            _projectViewModel.SelectedAudioPath = dialog.AudioFilePath;
            await _projectViewModel.CreateProjectAsync();
            await LoadProjectAudioAsync();
            MainTabControl.SelectedIndex = 1;
        }
    }

    private async void OpenSelectedMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_projectViewModel.CurrentProject != null)
        {
            await _projectViewModel.OpenProjectAsync(_projectViewModel.CurrentProject);
            await LoadProjectAudioAsync();
            MainTabControl.SelectedIndex = 1;
        }
    }

    private void ProjectsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var listBox = sender as System.Windows.Controls.ListBox;
        if (listBox == null) return;
        
        // Manually sync selection if binding failed (belt-and-suspenders approach)
        if (listBox.SelectedItem is Core.Models.Project selectedProject)
        {
            if (_projectViewModel.CurrentProject?.Id != selectedProject.Id)
                _projectViewModel.CurrentProject = selectedProject;
        }
    }

    private async void ProjectsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Double-click to open project from the list
        if (_projectViewModel.CurrentProject != null)
        {
            await _projectViewModel.OpenProjectAsync(_projectViewModel.CurrentProject);
            await LoadProjectAudioAsync();
            MainTabControl.SelectedIndex = 1;
        }
        else
        {
            MessageBox.Show("Please select a project first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
        MessageBox.Show("Podcast Video Editor - Phase 1", "About");
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
            FFmpegPathTextBox.Text = dialog.FileName;
        }
    }

    private async void ValidateFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        FFmpegStatusText.Text = "Validating FFmpeg...";
        var result = await FFmpegService.InitializeAsync(FFmpegPathTextBox.Text);
        FFmpegStatusText.Text = result.Message;
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        _audioPlayerViewModel.AudioLoaded -= OnAudioLoaded;

        _canvasViewModel?.Dispose();
        _visualizerViewModel?.Dispose();
        _audioPlayerViewModel?.Dispose();
        _timelineViewModel?.Dispose();
        _dbContext?.Dispose();
    }

    private async Task LoadProjectAudioAsync()
    {
        var project = _projectViewModel.CurrentProject;
        if (project == null || string.IsNullOrWhiteSpace(project.AudioPath))
        {
            _timelineViewModel.AudioPeaks = Array.Empty<float>();
            _audioPlayerViewModel.StopCommand.Execute(null);
            return;
        }

        _audioPlayerViewModel.StopCommand.Execute(null);
        _timelineViewModel.AudioPeaks = Array.Empty<float>();
        await _audioPlayerViewModel.LoadAudioAsync(project.AudioPath);
        // Timeline + waveform sync is done in OnAudioLoaded (so "Select audio" also updates waveform)
    }

    /// <summary>
    /// Called whenever audio is loaded (Open project or Select audio). Syncs timeline and waveform.
    /// </summary>
    private void OnAudioLoaded(object? sender, EventArgs e)
    {
        _visualizerViewModel.VisualizerWidth = 800;
        _visualizerViewModel.VisualizerHeight = 300;
        _visualizerViewModel.Initialize();

        var durationSeconds = Math.Max(1, _audioPlayerViewModel.TotalDuration);
        _timelineViewModel.TotalDuration = durationSeconds;
        _timelineViewModel.TimelineWidth = Math.Max(800, durationSeconds * 10);
        _timelineViewModel.AudioPeaks = Array.Empty<float>();
        _ = _timelineViewModel.RefreshAudioPeaksAsync();
    }
}
