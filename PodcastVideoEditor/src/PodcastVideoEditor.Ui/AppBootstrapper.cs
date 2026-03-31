#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Configuration;
using PodcastVideoEditor.Ui.Services;
using PodcastVideoEditor.Ui.Services.Update;
using PodcastVideoEditor.Ui.ViewModels;
using System;
using System.IO;
using System.Net.Http;

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
        RegisterConfiguration(services);
        RegisterInfrastructure(services, appDataPath);
        RegisterCoreServices(services);
        RegisterViewModels(services, appDataPath);
        RegisterUiServices(services, appDataPath);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static void RegisterConfiguration(IServiceCollection services)
    {
        services.AddSingleton(_ => AppConfiguration.Load(AppContext.BaseDirectory));
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddSingleton(_ => new HttpClient());
    }

    private static void RegisterInfrastructure(IServiceCollection services, string appDataPath)
    {
        var dbPath = Path.Combine(appDataPath, "app.db");
        services.AddSingleton<UserSettingsStore>(sp =>
        {
            var store = UserSettingsStore.Load(appDataPath);
            var appConfig = sp.GetRequiredService<AppConfiguration>();
            store.ApplyFallbacks(appConfig);
            return store;
        });
        services.AddSingleton<IRuntimeApiSettings>(sp => sp.GetRequiredService<UserSettingsStore>());
        services.AddDbContextFactory<AppDbContext>(opts =>
            opts.UseSqlite($"Data Source={dbPath}"));
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.AddSingleton<ProjectService>();
        services.AddSingleton<IProjectService>(sp => sp.GetRequiredService<ProjectService>());
        services.AddSingleton<ImageAssetIngestService>();

        services.AddSingleton<IAIProvider, YesScaleProvider>();
        services.AddSingleton<IImageSearchProvider, PexelsImageSearchProvider>();
        services.AddSingleton<IImageSearchProvider, PixabayImageSearchProvider>();
        services.AddSingleton<IImageSearchProvider, UnsplashImageSearchProvider>();
        services.AddSingleton<IAIImageSelectionService, AIImageSelectionService>();
        services.AddSingleton<IAIAnalysisOrchestrator, AIAnalysisOrchestrator>();

        services.AddSingleton<AudioService>();
        services.AddSingleton<IAudioPlaybackService>(sp => sp.GetRequiredService<AudioService>());
        services.AddSingleton<IAudioTimelinePreviewService>(sp => sp.GetRequiredService<AudioService>());
    }

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

    private static void RegisterUiServices(IServiceCollection services, string appDataPath)
    {
        services.AddSingleton<IUpdateService>(sp =>
            new GitHubReleaseUpdateService(
                sp.GetRequiredService<AppConfiguration>(),
                sp.GetRequiredService<IAppInfoService>(),
                sp.GetRequiredService<UserSettingsStore>(),
                appDataPath,
                sp.GetRequiredService<HttpClient>()));

        services.AddSingleton<AutosaveService>(sp =>
            new AutosaveService(
                () => sp.GetRequiredService<ProjectViewModel>().SaveProjectAsync(),
                delayMs: 2000));

        services.AddSingleton<MainWindow>();
    }
}
