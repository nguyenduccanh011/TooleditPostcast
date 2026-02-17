namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Configuration for rendering video from audio + image.
/// </summary>
public class RenderConfig
{
    /// <summary>
    /// Path to input audio file.
    /// </summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to background image (static).
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// Path where output MP4 will be saved.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Video output width in pixels (e.g., 1080, 720, 480).
    /// </summary>
    public int ResolutionWidth { get; set; } = 1080;

    /// <summary>
    /// Video output height in pixels (e.g., 1920, 1280, 854).
    /// </summary>
    public int ResolutionHeight { get; set; } = 1920;

    /// <summary>
    /// Aspect ratio string (e.g., "9:16", "16:9", "1:1", "4:5").
    /// </summary>
    public string AspectRatio { get; set; } = "9:16";

    /// <summary>
    /// Quality level: "Low" (CRF 28), "Medium" (CRF 23), "High" (CRF 18).
    /// </summary>
    public string Quality { get; set; } = "Medium";

    /// <summary>
    /// Target frame rate in fps (e.g., 30, 60).
    /// </summary>
    public int FrameRate { get; set; } = 30;

    /// <summary>
    /// Video codec: "h264", "h265", "vp9", etc.
    /// </summary>
    public string VideoCodec { get; set; } = "h264";

    /// <summary>
    /// Audio codec: "aac", "libmp3lame", "opus", etc.
    /// </summary>
    public string AudioCodec { get; set; } = "aac";

    /// <summary>
    /// Optional timeline-based visual segments. When set, renderer composes visuals by time ranges.
    /// </summary>
    public List<RenderVisualSegment> VisualSegments { get; set; } = [];

    /// <summary>
    /// Get CRF value based on quality setting.
    /// CRF 0-51 (lower = better quality but larger file).
    /// </summary>
    public int GetCrfValue()
    {
        return Quality switch
        {
            "Low" => 28,
            "Medium" => 23,
            "High" => 18,
            _ => 23
        };
    }
}

/// <summary>
/// A visual segment for timeline-based rendering.
/// </summary>
public class RenderVisualSegment
{
    /// <summary>
    /// Source media path (image or video).
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Segment start in output timeline (seconds).
    /// </summary>
    public double StartTime { get; set; }

    /// <summary>
    /// Segment end in output timeline (seconds).
    /// </summary>
    public double EndTime { get; set; }

    /// <summary>
    /// True when source is a video clip, false for image.
    /// </summary>
    public bool IsVideo { get; set; }

    /// <summary>
    /// Optional source offset for video clipping (seconds).
    /// </summary>
    public double SourceOffsetSeconds { get; set; }
}

/// <summary>
/// Progress report for render operations.
/// </summary>
public class RenderProgress
{
    /// <summary>
    /// Overall progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage { get; set; }

    /// <summary>
    /// Current frame being rendered.
    /// </summary>
    public int CurrentFrame { get; set; }

    /// <summary>
    /// Total frames to render.
    /// </summary>
    public int TotalFrames { get; set; }

    /// <summary>
    /// Estimated time remaining (seconds).
    /// </summary>
    public int EstimatedTimeRemaining { get; set; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Is render complete?
    /// </summary>
    public bool IsComplete { get; set; }
}
