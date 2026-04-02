#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>Named API key profiles. Each key unlocks a different group of models.</summary>
    public List<ApiKeyProfile> ApiKeyProfiles { get; set; } = new();
    IReadOnlyList<ApiKeyProfile> IRuntimeApiSettings.ApiKeyProfiles => ApiKeyProfiles;

    /// <summary>Profile ID of the API key used by the primary model.</summary>
    public string PrimaryProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Ordered fallback entries: model + profile ID.
    /// When the primary model fails, the provider tries these in order using each entry's API key.
    /// </summary>
    public List<ModelFallbackEntry> FallbackEntries { get; set; } = new();
    IReadOnlyList<ModelFallbackEntry> IRuntimeApiSettings.YesScaleFallbackEntries => FallbackEntries;

    // Kept for backward-compat deserialization from old settings files.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string>? FallbackModels { get; set; }

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
            var store = JsonSerializer.Deserialize<UserSettingsStore>(json, _jsonOpts) ?? new UserSettingsStore();
            store.MigrateFromLegacy();
            return store;
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
    /// Also merges bundled API key profiles and fallback entries when user has none configured.
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

        // Merge bundled API key profiles (only if user has no profiles yet)
        var bundledProfiles = appConfig.AIAnalysis.ApiKeyProfiles;
        if (ApiKeyProfiles.Count == 0 && bundledProfiles.Count > 0)
        {
            foreach (var bp in bundledProfiles)
            {
                if (string.IsNullOrWhiteSpace(bp.Name) || string.IsNullOrWhiteSpace(bp.ApiKey))
                    continue;

                ApiKeyProfiles.Add(new ApiKeyProfile
                {
                    Id      = Guid.NewGuid().ToString("N")[..8],
                    Name    = bp.Name,
                    ApiKey  = bp.ApiKey,
                    Enabled = true
                });
            }

            if (ApiKeyProfiles.Count > 0)
                PrimaryProfileId = ApiKeyProfiles[0].Id;
        }

        // Merge bundled fallback entries (only if user has no fallback entries yet)
        var bundledFallbacks = appConfig.AIAnalysis.FallbackEntries;
        if (FallbackEntries.Count == 0 && bundledFallbacks.Count > 0 && ApiKeyProfiles.Count > 0)
        {
            foreach (var bf in bundledFallbacks)
            {
                if (string.IsNullOrWhiteSpace(bf.ModelId)) continue;

                // Resolve profile by name (bundled config uses names, not IDs)
                var matchedProfile = ApiKeyProfiles.FirstOrDefault(
                    p => p.Name.Equals(bf.ProfileName, StringComparison.OrdinalIgnoreCase));

                if (matchedProfile == null) continue;

                FallbackEntries.Add(new ModelFallbackEntry
                {
                    ModelId   = bf.ModelId,
                    ProfileId = matchedProfile.Id
                });
            }
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

            YesScaleApiKey    = imported.YesScaleApiKey;
            YesScaleBaseUrl   = imported.YesScaleBaseUrl;
            YesScaleModel     = imported.YesScaleModel;
            ApiKeyProfiles    = imported.ApiKeyProfiles ?? new();
            PrimaryProfileId  = imported.PrimaryProfileId ?? string.Empty;
            FallbackEntries   = imported.FallbackEntries ?? new();
            FallbackModels    = imported.FallbackModels;
            FFmpegPath        = imported.FFmpegPath;
            PexelsApiKey      = imported.PexelsApiKey;
            PixabayApiKey     = imported.PixabayApiKey;
            UnsplashApiKey    = imported.UnsplashApiKey;
            MigrateFromLegacy();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import settings from {Path}", filePath);
            return false;
        }
    }

    // ── API key resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Ensures at least one profile exists when a primary key is available.
    /// Called after <see cref="ApplyFallbacks"/> so bundled keys from appsettings.json
    /// get a proper "Default" profile on first run.
    /// </summary>
    public void EnsureProfilesInitialized()
    {
        if (ApiKeyProfiles.Count == 0 && !string.IsNullOrWhiteSpace(YesScaleApiKey))
        {
            ApiKeyProfiles.Add(new ApiKeyProfile
            {
                Id      = "default",
                Name    = "Default",
                ApiKey  = YesScaleApiKey,
                Enabled = true
            });
            PrimaryProfileId = "default";
        }

        if (string.IsNullOrWhiteSpace(PrimaryProfileId) && ApiKeyProfiles.Count > 0)
            PrimaryProfileId = ApiKeyProfiles[0].Id;
    }

    /// <summary>
    /// Resolve the API key for a given profile ID.
    /// Falls back to the primary API key if profile not found.
    /// </summary>
    public string ResolveApiKey(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return YesScaleApiKey;

        var profile = ApiKeyProfiles.FirstOrDefault(
            p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase) && p.Enabled);
        return !string.IsNullOrWhiteSpace(profile?.ApiKey)
            ? profile.ApiKey
            : YesScaleApiKey;
    }

    // ── Migration ────────────────────────────────────────────────────────────

    /// <summary>
    /// Migrate from old single-key format to multi-profile format.
    /// Creates a default profile from YesScaleApiKey if no profiles exist.
    /// Converts old FallbackModels (string list) to FallbackEntries.
    /// </summary>
    private void MigrateFromLegacy()
    {
        // Auto-create a default profile from the main API key if no profiles exist
        if (ApiKeyProfiles.Count == 0 && !string.IsNullOrWhiteSpace(YesScaleApiKey))
        {
            var defaultProfile = new ApiKeyProfile
            {
                Id      = "default",
                Name    = "Default",
                ApiKey  = YesScaleApiKey,
                Enabled = true
            };
            ApiKeyProfiles.Add(defaultProfile);
            PrimaryProfileId = defaultProfile.Id;
            Log.Information("Migrated legacy API key to default profile");
        }

        // Ensure PrimaryProfileId points to a valid profile
        if (string.IsNullOrWhiteSpace(PrimaryProfileId) && ApiKeyProfiles.Count > 0)
            PrimaryProfileId = ApiKeyProfiles[0].Id;

        // Migrate old FallbackModels (string[]) → FallbackEntries (ModelFallbackEntry[])
        if (FallbackModels is { Count: > 0 } && FallbackEntries.Count == 0)
        {
            var defaultProfileId = PrimaryProfileId;
            foreach (var modelId in FallbackModels)
            {
                if (!string.IsNullOrWhiteSpace(modelId))
                    FallbackEntries.Add(new ModelFallbackEntry { ModelId = modelId, ProfileId = defaultProfileId });
            }
            FallbackModels = null; // clear legacy field
            Log.Information("Migrated {Count} legacy fallback models to fallback entries", FallbackEntries.Count);
        }
    }
}
