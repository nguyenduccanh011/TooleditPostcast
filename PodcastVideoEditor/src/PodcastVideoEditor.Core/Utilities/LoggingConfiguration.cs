#nullable enable
using Serilog;
using Serilog.Events;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Logging configuration for the application
/// </summary>
public static class LoggingConfiguration
{
    /// <summary>
    /// Initialize Serilog with file sink
    /// </summary>
    public static void ConfigureLogging(string appDataPath)
    {
        var logsPath = Path.Combine(appDataPath, "Logs");
        Directory.CreateDirectory(logsPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Console logging disabled for production (uncomment for debugging)
            // .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logsPath, "app-.txt"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 10_485_760 // 10 MB
            )
            .CreateLogger();

        Log.Information("Application started - AppDataPath: {AppDataPath}", appDataPath);
    }

    /// <summary>
    /// Close and flush logging
    /// </summary>
    public static async Task ShutdownLoggingAsync()
    {
        await Log.CloseAndFlushAsync();
    }
}
