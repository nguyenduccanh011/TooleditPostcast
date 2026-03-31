#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Ui.Configuration;
using Serilog;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Persists user-level API key configuration to %APPDATA%\PodcastVideoEditor\user_settings.json.
/// This is mutable at runtime; AI providers read keys here on every request so changes
/// take effect without restarting the app.
/// </summary>
public sealed class UserSettingsStore : IRuntimeApiSettings
{
    private const string FileName = "user_settings.json";
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // ── AI Analysis ──────────────────────────────────────────────────────────
    public string YesScaleApiKey      { get; set; } = string.Empty;
    public string YesScaleBaseUrl     { get; set; } = "https://api.yescale.vip/v1";
    public string YesScaleModel       { get; set; } = "gpt-4o-mini";
    public string FFmpegPath          { get; set; } = string.Empty;
    public DateTime? LastUpdateCheckUtc { get; set; }

    // ── Image Search ─────────────────────────────────────────────────────────
    public string PexelsApiKey   { get; set; } = string.Empty;
    public string PixabayApiKey  { get; set; } = string.Empty;
    public string UnsplashApiKey { get; set; } = string.Empty;

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Loads settings from <paramref name="appDataPath"/>. Returns defaults if file missing.</summary>
    public static UserSettingsStore Load(string appDataPath)
    {
        var path = FilePath(appDataPath);
        if (!File.Exists(path)) return new UserSettingsStore();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserSettingsStore>(json, _jsonOpts) ?? new UserSettingsStore();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load user settings from {Path}, using defaults", path);
            return new UserSettingsStore();
        }
    }

    /// <summary>Saves current settings to <paramref name="appDataPath"/>.</summary>
    public void Save(string appDataPath)
    {
        try
        {
            Directory.CreateDirectory(appDataPath);
            File.WriteAllText(FilePath(appDataPath), JsonSerializer.Serialize(this, _jsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save user settings to {Path}", FilePath(appDataPath));
            throw;
        }
    }

    private static string FilePath(string appDataPath) => Path.Combine(appDataPath, FileName);

    /// <summary>
    /// Fills in any empty API-key fields from the bundled <c>appsettings.json</c> defaults.
    /// This lets the release builder pre-configure keys so new installs work immediately.
    /// User-supplied values always take priority.
    /// </summary>
    public void ApplyFallbacks(AppConfiguration appConfig)
    {
        if (string.IsNullOrWhiteSpace(YesScaleApiKey) && !string.IsNullOrWhiteSpace(appConfig.AIAnalysis.YesScaleApiKey))
            YesScaleApiKey = appConfig.AIAnalysis.YesScaleApiKey;

        if (string.IsNullOrWhiteSpace(YesScaleBaseUrl) || YesScaleBaseUrl == "https://api.yescale.vip/v1")
        {
            if (!string.IsNullOrWhiteSpace(appConfig.AIAnalysis.BaseUrl))
                YesScaleBaseUrl = appConfig.AIAnalysis.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(YesScaleModel) || YesScaleModel == "gpt-4o-mini")
        {
            if (!string.IsNullOrWhiteSpace(appConfig.AIAnalysis.DefaultModel))
                YesScaleModel = appConfig.AIAnalysis.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(PexelsApiKey) && !string.IsNullOrWhiteSpace(appConfig.ImageSearch.PexelsApiKey))
            PexelsApiKey = appConfig.ImageSearch.PexelsApiKey;

        if (string.IsNullOrWhiteSpace(PixabayApiKey) && !string.IsNullOrWhiteSpace(appConfig.ImageSearch.PixabayApiKey))
            PixabayApiKey = appConfig.ImageSearch.PixabayApiKey;

        if (string.IsNullOrWhiteSpace(UnsplashApiKey) && !string.IsNullOrWhiteSpace(appConfig.ImageSearch.UnsplashApiKey))
            UnsplashApiKey = appConfig.ImageSearch.UnsplashApiKey;
    }

    /// <summary>Exports current settings to the specified file path.</summary>
    public void ExportTo(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, _jsonOpts));
    }

    /// <summary>
    /// Imports settings from a JSON file and overwrites current values.
    /// Returns true on success.
    /// </summary>
    public bool ImportFrom(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var imported = JsonSerializer.Deserialize<UserSettingsStore>(json, _jsonOpts);
            if (imported is null) return false;

            YesScaleApiKey  = imported.YesScaleApiKey;
            YesScaleBaseUrl = imported.YesScaleBaseUrl;
            YesScaleModel   = imported.YesScaleModel;
            FFmpegPath      = imported.FFmpegPath;
            PexelsApiKey    = imported.PexelsApiKey;
            PixabayApiKey   = imported.PixabayApiKey;
            UnsplashApiKey  = imported.UnsplashApiKey;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import settings from {Path}", filePath);
            return false;
        }
    }
}
