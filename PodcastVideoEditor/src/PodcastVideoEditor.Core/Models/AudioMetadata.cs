namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Metadata about an audio file (duration, sample rate, channels, etc.)
/// </summary>
public class AudioMetadata
{
    /// <summary>
    /// Total duration of audio file
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Sample rate in Hz (e.g., 44100, 48000)
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo)
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Bit depth (e.g., 16, 24, 32)
    /// </summary>
    public int BitDepth { get; set; }

    /// <summary>
    /// File path to audio file
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File name only (without path)
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Date file was created/imported
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
