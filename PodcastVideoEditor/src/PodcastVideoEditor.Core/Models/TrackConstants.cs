namespace PodcastVideoEditor.Core.Models;

/// <summary>
/// Strongly-typed constants for track types and segment kinds.
/// Use these instead of raw string literals to prevent typo bugs and
/// make refactoring safer across the codebase.
/// </summary>
public static class TrackTypes
{
    public const string Text = "text";
    public const string Visual = "visual";
    public const string Audio = "audio";
    public const string Effect = "effect";
}

public static class SegmentKinds
{
    public const string Text = "text";
    public const string Visual = "visual";
    public const string Audio = "audio";
    public const string Effect = "effect";
}
