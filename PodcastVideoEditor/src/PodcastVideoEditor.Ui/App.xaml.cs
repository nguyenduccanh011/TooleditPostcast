using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            // Enable GPU hardware acceleration for better performance
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            
            // Increase WPF render tier for smoother animations
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 60 });
            
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor");
            LoggingConfiguration.ConfigureLogging(appDataPath);

            base.OnStartup(e);

            Log.Information("=== Application Startup ===");
            Log.Information("GPU Rendering: {Mode}, Tier: {Tier}", 
                RenderCapability.IsPixelShaderVersionSupported(3, 0) ? "Hardware" : "Software",
                RenderCapability.Tier >> 16);
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
