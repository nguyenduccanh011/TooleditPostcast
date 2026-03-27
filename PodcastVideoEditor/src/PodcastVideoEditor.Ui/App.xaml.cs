using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Services;
using Serilog;

namespace PodcastVideoEditor.Ui;

/// <summary>
/// Application entry point. Bootstraps DI, initialises logging, and creates MainWindow.
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch ALL unhandled exceptions — log and keep app alive
        DispatcherUnhandledException += (s, ex) =>
        {
            Log.Error(ex.Exception, "[DispatcherUnhandledException] {Type}: {Msg}",
                ex.Exception.GetType().Name, ex.Exception.Message);
            ex.Handled = true; // prevent process termination
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            if (ex.ExceptionObject is Exception e2)
                Log.Fatal(e2, "[AppDomain.UnhandledException] IsTerminating={T}", ex.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            Log.Warning(ex.Exception, "[UnobservedTaskException]");
            ex.SetObserved();
        };

        try
        {
            // Enable GPU hardware acceleration for better performance
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            
            // Increase WPF render tier for smoother animations
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 60 });
            
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor");
            Directory.CreateDirectory(appDataPath);
            LoggingConfiguration.ConfigureLogging(appDataPath);

            base.OnStartup(e);

            Log.Information("=== Application Startup ===");
            Log.Information("GPU Rendering: {Mode}, Tier: {Tier}", 
                RenderCapability.IsPixelShaderVersionSupported(3, 0) ? "Hardware" : "Software",
                RenderCapability.Tier >> 16);
            Log.Information("App Data Path: {Path}", appDataPath);

            // Build DI container (composition root).
            _serviceProvider = AppBootstrapper.Build(appDataPath);

            // Apply pending EF Core migrations before the UI opens.
            using (var dbContext = _serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
            {
                DatabaseMigrationService.InitializeDatabase(dbContext);
            }

            // Resolve and show the main window.
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup Error: {Message}", ex.Message);
            MessageBox.Show($"Startup Error: {ex.GetType().Name}: {ex.Message}", "Error");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        Log.Information("=== Application Exit ===");
        try
        {
            (_serviceProvider as IDisposable)?.Dispose();
            LoggingConfiguration.ShutdownLoggingAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown.
        }
    }
}
