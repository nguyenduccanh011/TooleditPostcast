#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// HTTP client wrapper that communicates with the CapCut API standalone Flask server.
/// Manages server lifecycle (start/stop) and provides typed methods for each API endpoint.
/// </summary>
public sealed class CapCutExportService : IDisposable
{
    private readonly HttpClient _http;
    private Process? _serverProcess;
    private bool _disposed;

    private const string DefaultBaseUrl = "http://127.0.0.1:9001";
    private const int StartupWaitMs = 8000;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Path to the capcut_api_standalone directory containing capcut_server.py.
    /// </summary>
    public string ServerDirectory { get; set; } = string.Empty;

    /// <summary>
    /// CapCut's draft projects folder where drafts are moved so CapCut can see them.
    /// e.g. C:\Users\...\AppData\Local\CapCut\User Data\Projects\com.lveditor.draft
    /// </summary>
    public string? CapCutDraftFolder { get; set; }

    /// <summary>Last error message from server startup attempt.</summary>
    public string? LastError { get; private set; }

    public CapCutExportService(HttpClient http)
    {
        _http = http;
    }

    // ──────────────────── Server lifecycle ────────────────────

    /// <summary>
    /// Kills any orphaned Python capcut_server processes left from previous sessions.
    /// </summary>
    private void KillOrphanedServers()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("python"))
            {
                try
                {
                    // Check if this python process has capcut_server in its command line
                    // Use MainModule as a lightweight check - if it fails, skip
                    var cmdLine = proc.StartInfo?.Arguments ?? string.Empty;
                    // For already-running processes, StartInfo is empty. Use WMI-free approach:
                    // just check if there's a python listening on our port that we didn't start
                    if (_serverProcess == null || _serverProcess.HasExited || proc.Id != _serverProcess.Id)
                    {
                        // We can't easily get command line, so we'll just kill ALL python processes
                        // that are NOT our own. This is too aggressive.
                        // Instead, let's use netstat approach below.
                    }
                }
                catch { /* access denied, skip */ }
            }
        }
        catch { /* ignore */ }

        // Kill whatever is listening on our port
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"for /f \"tokens=5\" %a in ('netstat -aon ^| findstr :9001 ^| findstr LISTENING') do taskkill /F /PID %a\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var killProc = Process.Start(psi);
            killProc?.WaitForExit(5000);
            Log.Information("Cleaned up any orphaned server processes on port 9001");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up orphaned server processes");
        }
    }

    /// <summary>
    /// Starts the Flask server process if not already running.
    /// </summary>
    public async Task<bool> EnsureServerRunningAsync(CancellationToken ct = default)
    {
        // If we have a live process that we started, check if it's responding
        if (_serverProcess is { HasExited: false } && await IsServerAliveAsync(ct))
            return true;

        // Kill any orphaned server from a previous session before starting fresh
        StopServer();
        KillOrphanedServers();
        await Task.Delay(500, ct); // Give OS time to release the port

        if (string.IsNullOrEmpty(ServerDirectory) || !Directory.Exists(ServerDirectory))
        {
            Log.Error("CapCut API server directory not found: {Dir}", ServerDirectory);
            return false;
        }

        var scriptPath = Path.Combine(ServerDirectory, "capcut_server.py");
        if (!File.Exists(scriptPath))
        {
            Log.Error("capcut_server.py not found at {Path}", scriptPath);
            return false;
        }

        try
        {
            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = ServerDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            _serverProcess.Start();
            _ = Task.Run(() => LogProcessStream(_serverProcess, "stdout", _serverProcess.StandardOutput), ct);
            _ = Task.Run(() => LogProcessStream(_serverProcess, "stderr", _serverProcess.StandardError), ct);
            Log.Information("Started CapCut API server (PID {Pid}) from {Dir}", _serverProcess.Id, ServerDirectory);

            // Wait for server to become responsive
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < StartupWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                if (_serverProcess.HasExited)
                {
                    Log.Error("CapCut API server process exited prematurely with code {Code}", _serverProcess.ExitCode);
                    LastError = $"Python process exited with code {_serverProcess.ExitCode}. Check logs for details.";
                    return false;
                }
                await Task.Delay(500, ct);
                if (await IsServerAliveAsync(ct))
                    return true;
            }

            Log.Warning("CapCut API server started but not responding within {Ms}ms", StartupWaitMs);
            LastError = $"Server started but not responding within {StartupWaitMs}ms";
            return await IsServerAliveAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start CapCut API server");
            return false;
        }
    }

    public async Task<bool> IsServerAliveAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/get_transition_types", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void StopServer()
    {
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
                Log.Information("Stopped CapCut API server");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error stopping CapCut API server");
            }
        }
        _serverProcess = null;
    }

    // ──────────────────── API Methods ────────────────────

    public Task<JsonDocument?> CreateDraftAsync(string draftName, int width = 1080, int height = 1920, CancellationToken ct = default)
        => PostJsonAsync("/create_draft", new { draft_name = draftName, width, height }, ct);

    public Task<JsonDocument?> AddVideoAsync(string draftId, string videoUrl, double start, double end,
        int width = 1080, int height = 1920, double targetStart = 0, string trackName = "main_video",
        double speed = 1.0, double volume = 1.0, string? transition = null, double transitionDuration = 0,
        double transformX = 0, double transformY = 0, double scaleX = 1.0, double scaleY = 1.0,
        int relativeIndex = 0,
        CancellationToken ct = default)
        => PostJsonAsync("/add_video", new
        {
            draft_id = draftId, draft_folder = CapCutDraftFolder,
            video_url = videoUrl,
            start, end, width, height, target_start = targetStart,
            track_name = trackName, speed, volume,
            transition, transition_duration = transitionDuration,
            transform_x = transformX, transform_y = transformY,
            scale_x = scaleX, scale_y = scaleY,
            relative_index = relativeIndex
        }, ct);

    public Task<JsonDocument?> AddAudioAsync(string draftId, string audioUrl, double start, double end,
        double targetStart = 0, string trackName = "main_audio", double volume = 1.0,
        double fadeIn = 0, double fadeOut = 0, CancellationToken ct = default)
        => PostJsonAsync("/add_audio", new
        {
            draft_id = draftId, draft_folder = CapCutDraftFolder,
            audio_url = audioUrl,
            start, end, duration = end - start,
            target_start = targetStart,
            track_name = trackName, volume
        }, ct);

    public Task<JsonDocument?> AddTextAsync(string draftId, string text, double start, double duration,
        string trackName = "main_text", string? fontFamily = null, double fontSize = 8.0,
        string? color = null, double transformX = 0, double transformY = 0,
        bool isBold = false, bool isItalic = false, bool isUnderline = false,
        bool isSubtitle = false, string? subtitleGroupId = null,
        double fixedWidth = 0, double fixedHeight = 0,
        CancellationToken ct = default)
        => PostJsonAsync("/add_text", new
        {
            draft_id = draftId, text, start, end = start + duration,
            track_name = trackName, font = fontFamily,
            font_size = fontSize, color,
            transform_x = transformX, transform_y = transformY,
            bold = isBold, italic = isItalic, underline = isUnderline,
            is_subtitle = isSubtitle,
            subtitle_group_id = string.IsNullOrWhiteSpace(subtitleGroupId) ? null : subtitleGroupId,
            fixed_width = fixedWidth > 0 ? fixedWidth : (double?)null,
            fixed_height = fixedHeight > 0 ? fixedHeight : (double?)null
        }, ct);

    public Task<JsonDocument?> AddImageAsync(string draftId, string imageUrl, double start, double duration,
        int width = 1080, int height = 1920, string trackName = "main_image",
        double transformX = 0, double transformY = 0, double scaleX = 1.0, double scaleY = 1.0,
        string? maskType = null, double maskCenterX = 0, double maskCenterY = 0,
        double maskSize = 0.5, double? maskRectWidth = null, double maskRotation = 0,
        double maskFeather = 0, bool maskInvert = false, double? maskRoundCorner = null,
        int relativeIndex = 0,
        CancellationToken ct = default)
        => PostJsonAsync("/add_image", new
        {
            draft_id = draftId, draft_folder = CapCutDraftFolder,
            image_url = imageUrl,
            start, end = start + duration, width, height, track_name = trackName,
            transform_x = transformX, transform_y = transformY,
            scale_x = scaleX, scale_y = scaleY,
            mask_type = maskType,
            mask_center_x = maskCenterX,
            mask_center_y = maskCenterY,
            mask_size = maskSize,
            mask_rect_width = maskRectWidth,
            mask_rotation = maskRotation,
            mask_feather = maskFeather,
            mask_invert = maskInvert,
            mask_round_corner = maskRoundCorner,
            relative_index = relativeIndex
        }, ct);

    public Task<JsonDocument?> AddShapeBarAsync(
        string draftId,
        double start,
        double duration,
        int width,
        int height,
        int barHeightPx,
        string trackName,
        double transformX = 0,
        double transformY = 0,
        int relativeIndex = 0,
        string color = "#000000",
        CancellationToken ct = default)
        => PostJsonAsync("/add_shape_bar", new
        {
            draft_id = draftId,
            start,
            end = start + duration,
            width,
            height,
            bar_height_px = barHeightPx,
            transform_x = transformX,
            transform_y = transformY,
            track_name = trackName,
            relative_index = relativeIndex,
            color
        }, ct);

    public Task<JsonDocument?> AddVideoKeyframesBatchAsync(
        string draftId,
        string trackName,
        System.Collections.Generic.IReadOnlyList<string> propertyTypes,
        System.Collections.Generic.IReadOnlyList<double> times,
        System.Collections.Generic.IReadOnlyList<string> values,
        double targetStart,
        CancellationToken ct = default)
        => PostJsonAsync("/add_video_keyframe", new
        {
            draft_id = draftId,
            track_name = trackName,
            property_types = propertyTypes,
            times,
            values,
            target_start = targetStart
        }, ct);

    public Task<JsonDocument?> SaveDraftAsync(string draftId, CancellationToken ct = default)
        => PostJsonAsync("/save_draft", new { draft_id = draftId, draft_folder = CapCutDraftFolder }, ct);

    public Task<JsonDocument?> GetDraftStatusAsync(string draftId, CancellationToken ct = default)
        => PostJsonAsync("/get_draft_status", new { draft_id = draftId }, ct);

    // ──────────────────── Process logging ────────────────────

    private static async Task LogProcessStream(Process proc, string streamName, StreamReader reader)
    {
        try
        {
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line != null)
                    Log.Debug("[CapCut-{Stream}] {Line}", streamName, line);
            }
        }
        catch { /* process ended */ }
    }

    // ──────────────────── HTTP helper ────────────────────

    private async Task<JsonDocument?> PostJsonAsync(string endpoint, object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{BaseUrl}{endpoint}", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("CapCut API {Endpoint} returned {StatusCode}: {Body}", endpoint, response.StatusCode, body);
                return null;
            }

            return JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CapCut API call to {Endpoint} failed", endpoint);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        _serverProcess?.Dispose();
    }
}
