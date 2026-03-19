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
    /// Scaling behavior when source media aspect ratio does not match output.
    /// Fill = cover and crop, Fit = contain with padding, Stretch = force fill.
    /// </summary>
    public string ScaleMode { get; set; } = "Fill";

    /// <summary>
    /// Optional timeline-based visual segments. When set, renderer composes visuals by time ranges.
    /// </summary>
    public List<RenderVisualSegment> VisualSegments { get; set; } = [];

    /// <summary>
    /// Text overlay segments. Each renders as drawtext filter in the output video.
    /// </summary>
    public List<RenderTextSegment> TextSegments { get; set; } = [];

    /// <summary>
    /// Additional audio clip segments mixed into the output (BGM, sound effects, etc).
    /// </summary>
    public List<RenderAudioSegment> AudioSegments { get; set; } = [];

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

    /// <summary>
    /// Overlay X position expression for FFmpeg (default null = full-frame, no offset).
    /// </summary>
    public string? OverlayX { get; set; }

    /// <summary>
    /// Overlay Y position expression for FFmpeg (default null = full-frame, no offset).
    /// </summary>
    public string? OverlayY { get; set; }

    /// <summary>
    /// Explicit scale width (pixels). Null = use full render resolution.
    /// </summary>
    public int? ScaleWidth { get; set; }

    /// <summary>
    /// Explicit scale height (pixels). Null = use full render resolution.
    /// </summary>
    public int? ScaleHeight { get; set; }

    /// <summary>
    /// Scaling behavior inside explicit overlay bounds.
    /// Fill = cover and crop, Fit = contain with padding, Stretch = distort to bounds.
    /// </summary>
    public string ScaleMode { get; set; } = "Fill";
}

/// <summary>
/// A text overlay segment for timeline-based rendering (maps to FFmpeg drawtext filter).
/// </summary>
public class RenderTextSegment
{
    /// <summary>Text to overlay.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Start time in output timeline (seconds).</summary>
    public double StartTime { get; set; }

    /// <summary>End time in output timeline (seconds).</summary>
    public double EndTime { get; set; }

    /// <summary>Font size in pixels (default 48).</summary>
    public int FontSize { get; set; } = 48;

    /// <summary>Font color in hex format, e.g. "white" or "0xFFFFFF".</summary>
    public string FontColor { get; set; } = "white";

    /// <summary>Optional font file path. When null, FFmpeg uses a system default.</summary>
    public string? FontFilePath { get; set; }

    /// <summary>Font family name for resolving font file (e.g. "Arial", "Verdana").</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Whether text should be rendered bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether text should be rendered italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>Horizontal position expression passed to drawtext (default: centered).</summary>
    public string XExpr { get; set; } = "(w-text_w)/2";

    /// <summary>Vertical position expression passed to drawtext (default: near bottom).</summary>
    public string YExpr { get; set; } = "h*0.85-text_h/2";

    /// <summary>Box background: true to draw a semi-transparent box behind text.</summary>
    public bool DrawBox { get; set; } = true;

    /// <summary>Box color with alpha (ARGB). Default: 50% black.</summary>
    public string BoxColor { get; set; } = "black@0.5";
}

/// <summary>
/// An audio clip segment for timeline-based rendering (BGM, SFX, extra voice tracks).
/// Mixed into the output via FFmpeg amix/adelay filters.
/// </summary>
public class RenderAudioSegment
{
    /// <summary>Full path to the audio file.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>When the clip should START playing in the output timeline (seconds).</summary>
    public double StartTime { get; set; }

    /// <summary>When the clip should END in the output timeline (seconds).</summary>
    public double EndTime { get; set; }

    /// <summary>Volume level (0.0–1.0).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Fade-in duration in seconds from StartTime.</summary>
    public double FadeInDuration { get; set; }

    /// <summary>Fade-out duration in seconds before EndTime.</summary>
    public double FadeOutDuration { get; set; }

    /// <summary>Offset into the source file where playback begins (seconds).</summary>
    public double SourceOffsetSeconds { get; set; }

    /// <summary>Whether the audio should loop to fill its [StartTime, EndTime] window.</summary>
    public bool IsLooping { get; set; }
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
    /// True when render is still active but FFmpeg is not emitting steady progress updates.
    /// </summary>
    public bool IsIndeterminate { get; set; }

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
