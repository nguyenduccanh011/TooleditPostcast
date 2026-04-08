#nullable enable
using Serilog;
using Serilog.Events;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Logging configuration for the application
/// </summary>
public static class LoggingConfiguration
{
    private const int MainLogRetentionDays = 30;
    private const int AiLogRetentionDays = 14;

    /// <summary>
    /// Initialize Serilog with file sink
    /// </summary>
    public static void ConfigureLogging(string appDataPath)
    {
        var logsPath = Path.Combine(appDataPath, "Logs");
        Directory.CreateDirectory(logsPath);
        CleanupLegacyAndStaleLogs(logsPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            // Main log: Information+ for all messages
            .WriteTo.Logger(lc => lc
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: Path.Combine(logsPath, "app-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: MainLogRetentionDays,
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
                    retainedFileCountLimit: AiLogRetentionDays,
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

    private static void CleanupLegacyAndStaleLogs(string logsPath)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;

            foreach (var path in Directory.EnumerateFiles(logsPath, "*.txt", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var isMainLog = fileName.StartsWith("app-", StringComparison.OrdinalIgnoreCase);
                var isAiLog = fileName.StartsWith("ai-prompts-", StringComparison.OrdinalIgnoreCase);
                var lastWriteUtc = File.GetLastWriteTimeUtc(path);
                var ageDays = (nowUtc - lastWriteUtc).TotalDays;

                var shouldDelete = isMainLog
                    ? ageDays > MainLogRetentionDays + 5
                    : isAiLog
                        ? ageDays > AiLogRetentionDays + 5
                        : ageDays > 7;

                if (shouldDelete)
                {
                    File.Delete(path);
                }
            }

            foreach (var path in Directory.EnumerateFiles(logsPath, "*.log", SearchOption.TopDirectoryOnly))
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(path);
                if ((nowUtc - lastWriteUtc).TotalDays > 7)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only; never block app startup.
        }
    }
}
