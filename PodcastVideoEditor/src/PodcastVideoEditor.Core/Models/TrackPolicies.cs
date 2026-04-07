namespace PodcastVideoEditor.Core.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Stable role identifiers for timeline tracks.
/// Roles drive template mapping, AI routing, and UX semantics.
/// </summary>
public static class TrackRoles
{
    public const string Unspecified = "unspecified";
    public const string BrandOverlay = "brand_overlay";
    public const string TitleOverlay = "title_overlay";
    public const string ScriptText = "script_text";
    public const string AiContent = "ai_content";
    public const string Visualizer = "visualizer";
    public const string BackgroundContent = "background_content";
}

/// <summary>
/// Role compatibility rules by track type.
/// Keeps UI and business logic aligned so users cannot assign mismatched roles.
/// </summary>
public static class TrackRolePolicies
{
    private static readonly StringComparer RoleComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> TextRoles = new(RoleComparer)
    {
        TrackRoles.Unspecified,
        TrackRoles.ScriptText,
        TrackRoles.TitleOverlay,
    };

    private static readonly HashSet<string> VisualRoles = new(RoleComparer)
    {
        TrackRoles.Unspecified,
        TrackRoles.AiContent,
    };

    private static readonly HashSet<string> AudioRoles = new(RoleComparer)
    {
        TrackRoles.Unspecified,
    };

    private static readonly HashSet<string> EffectRoles = new(RoleComparer)
    {
        TrackRoles.Unspecified,
        TrackRoles.Visualizer,
    };

    private static readonly HashSet<string> AllRoles = new(RoleComparer)
    {
        TrackRoles.Unspecified,
        TrackRoles.BrandOverlay,
        TrackRoles.TitleOverlay,
        TrackRoles.ScriptText,
        TrackRoles.AiContent,
        TrackRoles.Visualizer,
        TrackRoles.BackgroundContent,
    };

    public static IReadOnlySet<string> GetAllowedRoles(string? trackType)
    {
        var normalizedTrackType = Normalize(trackType);
        return normalizedTrackType switch
        {
            TrackTypes.Text => TextRoles,
            TrackTypes.Visual => VisualRoles,
            TrackTypes.Audio => AudioRoles,
            TrackTypes.Effect => EffectRoles,
            _ => AllRoles,
        };
    }

    public static string GetDefaultRole(string? trackType)
    {
        var normalizedTrackType = Normalize(trackType);
        return normalizedTrackType switch
        {
            TrackTypes.Text => TrackRoles.ScriptText,
            TrackTypes.Visual => TrackRoles.Unspecified,
            TrackTypes.Effect => TrackRoles.Visualizer,
            _ => TrackRoles.Unspecified,
        };
    }

    public static string NormalizeRoleForTrackType(string? role, string? trackType)
    {
        var normalizedRole = Normalize(role);
        if (string.IsNullOrWhiteSpace(normalizedRole))
            normalizedRole = TrackRoles.Unspecified;

        return GetAllowedRoles(trackType).Contains(normalizedRole)
            ? normalizedRole
            : GetDefaultRole(trackType);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}

/// <summary>
/// Duration/span policy for track segments.
/// </summary>
public static class TrackSpanModes
{
    /// <summary>
    /// Track follows the project's effective max end time.
    /// </summary>
    public const string ProjectDuration = "project_duration";

    /// <summary>
    /// Track preserves template-defined timing.
    /// </summary>
    public const string TemplateDuration = "template_duration";

    /// <summary>
    /// Segment-level timing is authoritative.
    /// </summary>
    public const string SegmentBound = "segment_bound";

    /// <summary>
    /// User manually controls timing, no auto stretching.
    /// </summary>
    public const string Manual = "manual";
}
