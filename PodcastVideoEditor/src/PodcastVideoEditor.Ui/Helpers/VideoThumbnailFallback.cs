using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

namespace PodcastVideoEditor.Ui.Helpers;

/// <summary>
/// Fallback: capture video frame using WPF MediaPlayer when FFmpeg is unavailable or fails.
/// Must be called from UI thread. Based on CodeProject: Getting Thumbnail from Video using MediaPlayer.
/// </summary>
public static class VideoThumbnailFallback
{
    private const int ThumbnailWidth = 120;
    private const int ThumbnailHeight = 90;
    private const int WaitMs = 300;

    /// <summary>
    /// Capture a frame from the video at the given time and save to the specified path.
    /// Returns true if the file was written successfully. Call from UI thread.
    /// </summary>
    public static bool TryCaptureFrameToFile(string videoPath, double timeSeconds, string outputImagePath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return false;
        if (string.IsNullOrWhiteSpace(outputImagePath))
            return false;

        var dir = Path.GetDirectoryName(outputImagePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        MediaPlayer? player = null;
        try
        {
            var uri = new Uri(Path.GetFullPath(videoPath), UriKind.Absolute);
            player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            player.Open(uri);
            player.Pause();
            player.Position = TimeSpan.FromSeconds(Math.Max(0, timeSeconds));
            WaitWithDispatcher(WaitMs);

            var rtb = new RenderTargetBitmap(ThumbnailWidth, ThumbnailHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, ThumbnailWidth, ThumbnailHeight));
            rtb.Render(dv);

            var frame = BitmapFrame.Create(rtb);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using (var stream = File.Create(outputImagePath))
                encoder.Save(stream);

            Log.Debug("VideoThumbnailFallback: saved frame to {Path}", outputImagePath);
            return File.Exists(outputImagePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoThumbnailFallback failed: {Video} @ {Time}s", videoPath, timeSeconds);
            return false;
        }
        finally
        {
            player?.Close();
        }
    }

    private static void WaitWithDispatcher(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
