#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Services;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.IO;

namespace PodcastVideoEditor.Ui;

/// <summary>
/// Composition root: registers all application dependencies into the DI container.
/// Keeps App.xaml.cs and MainWindow.xaml.cs free of construction and wiring logic.
/// </summary>
public static class AppBootstrapper
{
    /// <summary>
    /// Builds and returns the application's <see cref="IServiceProvider"/>.
    /// Call once from <see cref="App.OnStartup"/>.
    /// </summary>
    public static IServiceProvider Build(string appDataPath)
    {
        var services = new ServiceCollection();
        RegisterInfrastructure(services, appDataPath);
        RegisterCoreServices(services);
        RegisterViewModels(services, appDataPath);
        RegisterUiServices(services);
        return services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateOnBuild = true });
    }

    // ── Infrastructure ────────────────────────────────────────────────────────────

    private static void RegisterInfrastructure(IServiceCollection services, string appDataPath)
    {
        var dbPath = Path.Combine(appDataPath, "app.db");
        services.AddSingleton<UserSettingsStore>(_ => UserSettingsStore.Load(appDataPath));
        services.AddSingleton<IRuntimeApiSettings>(sp => sp.GetRequiredService<UserSettingsStore>());
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseSqlite($"Data Source={dbPath}"),
            ServiceLifetime.Singleton);
    }

    // ── Core services ─────────────────────────────────────────────────────────────

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // ProjectService: single instance wrapping the shared DbContext.
        services.AddSingleton<ProjectService>();
        services.AddSingleton<IProjectService>(sp => sp.GetRequiredService<ProjectService>());

        // ImageAssetIngestService: stateless, default HTTP client.
        services.AddSingleton<ImageAssetIngestService>();

        services.AddSingleton<IAIProvider, YesScaleProvider>();
        services.AddSingleton<IImageSearchProvider, PexelsImageSearchProvider>();
        services.AddSingleton<IImageSearchProvider, PixabayImageSearchProvider>();
        services.AddSingleton<IImageSearchProvider, UnsplashImageSearchProvider>();
        services.AddSingleton<IAIImageSelectionService, AIImageSelectionService>();
        services.AddSingleton<IAIAnalysisOrchestrator, AIAnalysisOrchestrator>();

        // AudioService: single instance because it holds the WaveOut device.
        // Register as concrete type first so it can be resolved as either interface.
        services.AddSingleton<AudioService>();
        services.AddSingleton<IAudioPlaybackService>(sp => sp.GetRequiredService<AudioService>());
        services.AddSingleton<IAudioTimelinePreviewService>(sp => sp.GetRequiredService<AudioService>());
    }

    // ── ViewModels ────────────────────────────────────────────────────────────────

    private static void RegisterViewModels(IServiceCollection services, string appDataPath)
    {
        services.AddSingleton<RenderViewModel>();
        services.AddSingleton<VisualizerViewModel>();
        services.AddSingleton<CanvasViewModel>();
        services.AddSingleton<ProjectViewModel>();
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<UserSettingsStore>(),
                sp.GetRequiredService<IAIProvider>(),
                appDataPath));
        services.AddSingleton<AudioPlayerViewModel>(sp =>
            new AudioPlayerViewModel(sp.GetRequiredService<IAudioPlaybackService>()));
        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<MainViewModel>(sp =>
        {
            var mainVm = new MainViewModel(
                sp.GetRequiredService<ProjectViewModel>(),
                sp.GetRequiredService<RenderViewModel>(),
                sp.GetRequiredService<CanvasViewModel>(),
                sp.GetRequiredService<AudioPlayerViewModel>(),
                sp.GetRequiredService<VisualizerViewModel>(),
                sp.GetRequiredService<TimelineViewModel>());

            // Wire cross-ViewModel attachments that require both sides to exist.
            var canvas = sp.GetRequiredService<CanvasViewModel>();
            var project = sp.GetRequiredService<ProjectViewModel>();
            var timeline = sp.GetRequiredService<TimelineViewModel>();
            var render = sp.GetRequiredService<RenderViewModel>();

            canvas.AttachProjectAndTimeline(project, timeline);
            render.AttachTimeline(timeline);
            render.AttachCanvas(canvas);

            return mainVm;
        });
    }

    // ── UI services ───────────────────────────────────────────────────────────────

    private static void RegisterUiServices(IServiceCollection services)
    {
        services.AddSingleton<AutosaveService>(sp =>
            new AutosaveService(
                () => sp.GetRequiredService<ProjectViewModel>().SaveProjectAsync(),
                delayMs: 2000));

        services.AddSingleton<MainWindow>();
    }
}
