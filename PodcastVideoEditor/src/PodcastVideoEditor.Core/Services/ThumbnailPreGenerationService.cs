#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PodcastVideoEditor.Core.Models;
using Serilog;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Background service for pre-generating video thumbnails to improve timeline responsiveness.
/// Runs thumbnail extraction in background with configurable concurrency to avoid blocking UI.
/// </summary>
public class ThumbnailPreGenerationService
{
    private readonly ConcurrentQueue<ThumbnailRequest> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrent;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    
    private const int DefaultMaxConcurrent = 2; // 2 concurrent FFmpeg processes
    private const int StripFrameCount = 3; // Reduced from 5 for faster generation

    public ThumbnailPreGenerationService(int maxConcurrent = DefaultMaxConcurrent)
    {
        _maxConcurrent = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }

    /// <summary>
    /// Pre-generate thumbnails for all video segments in a project.
    /// Generates: first frame + 5 strip positions for each video segment.
    /// </summary>
    public async Task PreGenerateThumbnailsForProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (project?.Tracks == null)
            return;

        var videosToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = new List<ThumbnailRequest>();

        // Collect all video segments
        foreach (var track in project.Tracks)
        {
            if (track.Segments == null) continue;
            
            foreach (var segment in track.Segments)
            {
                if (string.IsNullOrWhiteSpace(segment.BackgroundAssetId))
                    continue;

                var asset = project.Assets?.FirstOrDefault(a => 
                    string.Equals(a.Id, segment.BackgroundAssetId, StringComparison.Ordinal));
                
                if (asset == null || !string.Equals(asset.Type, "Video", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(asset.FilePath))
                    continue;

                // Queue first frame (time 0) - used as single thumbnail
                var firstFrameKey = $"{asset.FilePath}|0.00";
                if (videosToProcess.Add(firstFrameKey))
                {
                    requests.Add(new ThumbnailRequest 
                    { 
                        VideoPath = asset.FilePath, 
                        TimeSeconds = 0,
                        Priority = ThumbnailPriority.High // First frame is high priority
                    });
                }

                // Queue strip positions (0%, 25%, 50%, 75%, 100% of segment)
                var duration = segment.EndTime - segment.StartTime;
                if (duration > 0)
                {
                    for (int i = 0; i < StripFrameCount; i++)
                    {
                        var timeInVideo = segment.StartTime + (duration * i / (StripFrameCount - 1.0));
                        var stripKey = $"{asset.FilePath}|{timeInVideo:F2}";
                        
                        if (videosToProcess.Add(stripKey))
                        {
                            requests.Add(new ThumbnailRequest
                            {
                                VideoPath = asset.FilePath,
                                TimeSeconds = timeInVideo,
                                Priority = ThumbnailPriority.Normal
                            });
                        }
                    }
                }
            }
        }

        // Sort by priority (high first)
        var sortedRequests = requests.OrderByDescending(r => r.Priority);
        
        foreach (var request in sortedRequests)
        {
            _queue.Enqueue(request);
        }

        Log.Information("ThumbnailPreGen: Queued {Count} thumbnails for project {Project}", 
            requests.Count, project.Name);

        // Start background worker if not running
        await StartWorkerAsync(cancellationToken);
    }

    /// <summary>
    /// Start background worker to process thumbnail queue.
    /// </summary>
    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        if (_workerTask != null && !_workerTask.IsCompleted)
            return;

        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token), _cts.Token);
        await Task.Yield(); // Allow worker to start
    }

    /// <summary>
    /// Process thumbnail generation queue with concurrency control.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var request))
                {
                    await _semaphore.WaitAsync(cancellationToken);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            // Check if already cached
                            var cachePath = FFmpegService.GetThumbnailCachePathFor(
                                request.VideoPath, request.TimeSeconds);
                            
                            if (!System.IO.File.Exists(cachePath))
                            {
                                var result = await FFmpegService.GetOrCreateVideoThumbnailPathAsync(
                                    request.VideoPath, 
                                    request.TimeSeconds, 
                                    cancellationToken);

                                if (result != null)
                                {
                                    Log.Debug("ThumbnailPreGen: Generated {Path} @ {Time:F2}s", 
                                        request.VideoPath, request.TimeSeconds);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "ThumbnailPreGen: Failed for {Path} @ {Time:F2}s", 
                                request.VideoPath, request.TimeSeconds);
                        }
                        finally
                        {
                            _semaphore.Release();
                        }
                    }, cancellationToken);

                    tasks.Add(task);

                    // Clean up completed tasks
                    tasks.RemoveAll(t => t.IsCompleted);
                }
                else
                {
                    // Queue empty, wait a bit before checking again
                    if (tasks.Count == 0)
                        break; // All done
                    
                    await Task.Delay(100, cancellationToken);
                }
            }

            // Wait for remaining tasks
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);

            Log.Information("ThumbnailPreGen: Completed processing queue");
        }
        catch (OperationCanceledException)
        {
            Log.Information("ThumbnailPreGen: Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ThumbnailPreGen: Worker error");
        }
    }

    /// <summary>
    /// Stop background worker and clear queue.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        while (_queue.TryDequeue(out _)) { }
        Log.Information("ThumbnailPreGen: Stopped");
    }

    /// <summary>
    /// Get current queue size.
    /// </summary>
    public int QueueSize => _queue.Count;

    private class ThumbnailRequest
    {
        public required string VideoPath { get; init; }
        public required double TimeSeconds { get; init; }
        public ThumbnailPriority Priority { get; init; }
    }

    private enum ThumbnailPriority
    {
        Normal = 0,
        High = 1
    }
}
