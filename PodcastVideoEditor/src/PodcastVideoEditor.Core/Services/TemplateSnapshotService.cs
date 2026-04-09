#nullable enable
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PodcastVideoEditor.Core.Services;

public sealed class TemplateSnapshotService
{
    public TemplateProjectSnapshot BuildSnapshot(Project project)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));

        var assetById = (project.Assets ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var excludedDynamicSegmentIds = (project.Tracks ?? [])
            .Where(t =>
                string.Equals(t.TrackRole, TrackRoles.AiContent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.TrackRole, TrackRoles.ScriptText, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.Segments ?? [])
            .Select(s => s.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        Log.Information(
            "TemplateSnapshotBuildStart: project={ProjectId} tracks={TrackCount} assets={AssetCount} elements={ElementCount}",
            project.Id,
            project.Tracks?.Count ?? 0,
            project.Assets?.Count ?? 0,
            project.Elements?.Count ?? 0);

        var tracks = project.Tracks?
            .OrderBy(t => t.Order)
            .Select(t => new TemplateTrackSnapshot
            {
                Id = t.Id,
                Order = t.Order,
                TrackType = t.TrackType,
                TrackRole = TrackRolePolicies.NormalizeRoleForTrackType(t.TrackRole, t.TrackType),
                SpanMode = t.SpanMode,
                Name = t.Name,
                IsLocked = t.IsLocked,
                IsVisible = t.IsVisible,
                ImageLayoutPreset = t.ImageLayoutPreset,
                AutoMotionEnabled = t.AutoMotionEnabled,
                MotionIntensity = t.MotionIntensity,
                OverlayColorHex = t.OverlayColorHex,
                OverlayOpacity = t.OverlayOpacity,
                TextStyleJson = t.TextStyleJson,
                Segments = string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.TrackRole, TrackRoles.AiContent, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.TrackRole, TrackRoles.ScriptText, StringComparison.OrdinalIgnoreCase)
                    ? []
                    : t.Segments?
                        .OrderBy(s => s.Order)
                        .Select(s =>
                        {
                            var segAsset = !string.IsNullOrWhiteSpace(s.BackgroundAssetId)
                                && assetById.TryGetValue(s.BackgroundAssetId, out var matchedAsset)
                                ? matchedAsset
                                : null;

                            return new TemplateSegmentSnapshot
                            {
                                Id = s.Id,
                                StartTime = s.StartTime,
                                EndTime = s.EndTime,
                                Text = s.Text,
                                BackgroundAssetId = null,
                                BackgroundAssetPath = segAsset?.FilePath,
                                BackgroundAssetType = segAsset?.Type,
                                TransitionType = s.TransitionType,
                                TransitionDuration = s.TransitionDuration,
                                Order = s.Order,
                                Kind = s.Kind,
                                Keywords = s.Keywords,
                                Volume = s.Volume,
                                FadeInDuration = s.FadeInDuration,
                                FadeOutDuration = s.FadeOutDuration,
                                SourceStartOffset = s.SourceStartOffset,
                                MotionPreset = s.MotionPreset,
                                MotionIntensity = s.MotionIntensity,
                                OverlayColorHex = s.OverlayColorHex,
                                OverlayOpacity = s.OverlayOpacity
                            };
                        })
                        .ToList() ?? []
            })
            .ToList() ?? [];

        foreach (var track in tracks)
        {
            Log.Information(
                "TemplateSnapshotTrack: trackId={TrackId} type={TrackType} role={TrackRole} span={SpanMode} segmentCount={SegmentCount}",
                track.Id,
                track.TrackType,
                track.TrackRole,
                track.SpanMode,
                track.Segments?.Count ?? 0);
        }

        var elements = project.Elements?
            .Where(e => string.IsNullOrWhiteSpace(e.SegmentId) || !excludedDynamicSegmentIds.Contains(e.SegmentId))
            .Select(e => new TemplateElementSnapshot
            {
                Type = e.Type,
                X = e.X,
                Y = e.Y,
                Width = e.Width,
                Height = e.Height,
                Rotation = e.Rotation,
                ZIndex = e.ZIndex,
                Opacity = e.Opacity,
                PropertiesJson = e.PropertiesJson,
                IsVisible = e.IsVisible,
                SegmentId = e.SegmentId
            })
            .ToList() ?? [];

        return new TemplateProjectSnapshot
        {
            RenderSettings = project.RenderSettings ?? new RenderSettings(),
            Tracks = tracks,
            Elements = elements
        };
    }

    public sealed class TemplateProjectSnapshot
    {
        public RenderSettings? RenderSettings { get; set; }
        public List<TemplateTrackSnapshot> Tracks { get; set; } = [];
        public List<TemplateElementSnapshot> Elements { get; set; } = [];
    }

    public sealed class TemplateTrackSnapshot
    {
        public string? Id { get; set; }
        public int Order { get; set; }
        public string? TrackType { get; set; }
        public string? TrackRole { get; set; }
        public string? SpanMode { get; set; }
        public string? Name { get; set; }
        public bool IsLocked { get; set; }
        public bool IsVisible { get; set; } = true;
        public string? ImageLayoutPreset { get; set; }
        public bool AutoMotionEnabled { get; set; }
        public double MotionIntensity { get; set; } = 0.3;
        public string? OverlayColorHex { get; set; }
        public double OverlayOpacity { get; set; }
        public string? TextStyleJson { get; set; }
        public List<TemplateSegmentSnapshot> Segments { get; set; } = [];
    }

    public sealed class TemplateSegmentSnapshot
    {
        public string? Id { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public string? Text { get; set; }
        public string? BackgroundAssetId { get; set; }
        public string? BackgroundAssetPath { get; set; }
        public string? BackgroundAssetType { get; set; }
        public string? TransitionType { get; set; }
        public double TransitionDuration { get; set; } = 0.5;
        public int Order { get; set; }
        public string? Kind { get; set; }
        public string? Keywords { get; set; }
        public double Volume { get; set; } = 1.0;
        public double FadeInDuration { get; set; }
        public double FadeOutDuration { get; set; }
        public double SourceStartOffset { get; set; }
        public string? MotionPreset { get; set; }
        public double? MotionIntensity { get; set; }
        public string? OverlayColorHex { get; set; }
        public double? OverlayOpacity { get; set; }
    }

    public sealed class TemplateElementSnapshot
    {
        public string? Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 100;
        public double Height { get; set; } = 50;
        public double Rotation { get; set; }
        public int ZIndex { get; set; }
        public double Opacity { get; set; } = 1.0;
        public string? PropertiesJson { get; set; }
        public bool IsVisible { get; set; } = true;
        public string? SegmentId { get; set; }
    }
}