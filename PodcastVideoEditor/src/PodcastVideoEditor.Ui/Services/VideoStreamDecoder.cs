#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Serilog;
using SkiaSharp;

namespace PodcastVideoEditor.Ui.Services;

/// <summary>
/// Decodes a video source to a sequential stream of fixed-size RGBA frames via FFmpeg
/// (cover-cropped + resampled to a constant fps), for frame-accurate compositor export.
/// Forward-only: <see cref="GetFrameAtSource"/> must be called with non-decreasing source times
/// (export renders frames in order). NOT thread-safe.
/// </summary>
public sealed class VideoStreamDecoder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _frameBytes;
    private readonly double _seekSeconds;
    private readonly byte[] _frame;

    private Process? _proc;
    private Stream? _stdout;
    private int _currentIndex = -1;
    private bool _eof;
    private SKImage? _currentImage;
    private bool _disposed;

    public VideoStreamDecoder(string ffmpegPath, string sourcePath, double seekSeconds, int width, int height, int fps)
    {
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);
        _fps = Math.Max(1, fps);
        _frameBytes = _width * _height * 4;
        _seekSeconds = Math.Max(0, seekSeconds);
        _frame = new byte[_frameBytes];

        try
        {
            var inv = CultureInfo.InvariantCulture;
            var vf = $"fps={_fps},scale={_width}:{_height}:force_original_aspect_ratio=increase,crop={_width}:{_height},format=rgba";
            var args = $"-hide_banner -loglevel error -ss {_seekSeconds.ToString("F3", inv)} -i \"{sourcePath}\" -an -vf \"{vf}\" -f rawvideo pipe:1";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? ""
            };
            _proc = Process.Start(psi);
            _stdout = _proc?.StandardOutput.BaseStream;
            // Drain stderr so the pipe never blocks.
            if (_proc != null)
                _ = System.Threading.Tasks.Task.Run(() => { try { _proc.StandardError.ReadToEnd(); } catch { } });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoStreamDecoder failed to start for {Src}", sourcePath);
            _eof = true;
        }
    }

    /// <summary>Returns the frame nearest the given source time (forward-only). May reuse the last frame.</summary>
    public SKImage? GetFrameAtSource(double sourceSeconds)
    {
        if (_disposed || _stdout == null)
            return _currentImage;

        var targetIndex = Math.Max(0, (int)Math.Round((sourceSeconds - _seekSeconds) * _fps));
        while (_currentIndex < targetIndex && !_eof)
        {
            if (ReadFull(_stdout, _frame, _frameBytes))
            {
                _currentIndex++;
                if (_currentIndex == targetIndex)
                    UpdateCurrentImage();
            }
            else
            {
                _eof = true;
            }
        }
        return _currentImage;
    }

    private void UpdateCurrentImage()
    {
        var prev = _currentImage;
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        _currentImage = SKImage.FromPixelCopy(info, _frame);
        prev?.Dispose();
    }

    private static bool ReadFull(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stdout?.Dispose(); } catch { }
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc?.Dispose();
        _currentImage?.Dispose();
    }
}
