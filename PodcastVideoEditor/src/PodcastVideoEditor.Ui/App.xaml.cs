using System;
using System.IO;
using System.Windows;
using PodcastVideoEditor.Core.Utilities;
using Serilog;

namespace PodcastVideoEditor.Ui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor");
            LoggingConfiguration.ConfigureLogging(appDataPath);

            base.OnStartup(e);

            Log.Information("=== Application Startup ===");
            Log.Information("App Data Path: {Path}", appDataPath);
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
            LoggingConfiguration.ShutdownLoggingAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown.
        }
    }
}
