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
            _dbContext.Database.EnsureCreated();

            var projectService = new ProjectService(_dbContext);
            _projectViewModel = new ProjectViewModel(projectService);
            _renderViewModel = new RenderViewModel();
            _audioService = new AudioService();
            _visualizerViewModel = new VisualizerViewModel(_audioService);
            _canvasViewModel = new CanvasViewModel(_visualizerViewModel);
            _audioPlayerViewModel = new AudioPlayerViewModel(_audioService);
            _timelineViewModel = new TimelineViewModel(_audioService, _projectViewModel);

            _mainViewModel = new MainViewModel(
                _projectViewModel,
                _renderViewModel,
                _canvasViewModel,
                _audioPlayerViewModel,
                _visualizerViewModel,
                _timelineViewModel);
            DataContext = _mainViewModel;

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
    }

    private async Task LoadProjectsSafeAsync()
    {
        try
        {
            await _projectViewModel.LoadProjectsAsync();
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
        try
        {
            AudioPlayerControl?.Cleanup();
        }
        catch
        {
        }

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
            return;

        await _audioPlayerViewModel.LoadAudioAsync(project.AudioPath);

        // Initialize visualizer when audio is loaded (for both Editor tab and Canvas tab)
        _visualizerViewModel.VisualizerWidth = 800;
        _visualizerViewModel.VisualizerHeight = 300;
        _visualizerViewModel.Initialize();

        // Sync timeline duration/width after audio load
        var durationSeconds = Math.Max(1, _audioPlayerViewModel.TotalDuration);
        _timelineViewModel.TotalDuration = durationSeconds;
        _timelineViewModel.TimelineWidth = Math.Max(800, durationSeconds * 10);
    }
}
