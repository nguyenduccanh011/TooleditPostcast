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
            .MinimumLevel.Debug()
            // Main log: Information+ for all messages
            .WriteTo.Logger(lc => lc
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: Path.Combine(logsPath, "app-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 10_485_760 // 10 MB
                ))
            // AI debug log: Debug+ filtered to events tagged with AICall property.
            // Contains full prompts and raw responses for prompt analysis/improvement.
            .WriteTo.Logger(lc => lc
                .MinimumLevel.Debug()
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("AICall"))
                .WriteTo.File(
                    path: Path.Combine(logsPath, "ai-prompts-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "──────────────────────────────────────────{NewLine}[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 52_428_800 // 50 MB
                ))
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
