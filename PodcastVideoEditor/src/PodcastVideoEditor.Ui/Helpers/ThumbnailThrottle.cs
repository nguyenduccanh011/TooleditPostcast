#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.Helpers
{
    /// <summary>
    /// Global gate that bounds how many FFmpeg thumbnail-extraction processes run at once.
    /// Multiple independent binding converters (segment thumbnails, video-frame-at-time, etc.)
    /// each fire fire-and-forget extraction tasks; without a shared cap a project with many
    /// video clips could spawn dozens of concurrent ffmpeg.exe processes, thrashing the disk
    /// and CPU and making the editor feel heavy. Routing every extraction through this gate
    /// keeps concurrency bounded across all pipelines.
    /// </summary>
    public static class ThumbnailThrottle
    {
        private static readonly SemaphoreSlim _gate =
            new(Math.Max(2, Math.Min(4, Environment.ProcessorCount / 4)));

        /// <summary>
        /// Run a thumbnail-extraction unit of work, blocking until a concurrency slot is free.
        /// </summary>
        public static async Task<T> RunThrottledAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
