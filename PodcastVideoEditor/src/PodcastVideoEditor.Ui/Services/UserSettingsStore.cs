#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PodcastVideoEditor.Core.Services.AI;
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
    public string YesScaleApiKey   { get; set; } = string.Empty;
    public string YesScaleBaseUrl  { get; set; } = "https://api.yescale.vip/v1";
    public string YesScaleModel    { get; set; } = "gpt-4o-mini";

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
}
