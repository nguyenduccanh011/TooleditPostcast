#nullable enable
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for project-specific CRUD operations.
    /// </summary>
    public class ProjectService : IProjectService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public ProjectService(IDbContextFactory<AppDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        /// <summary>
        /// Add a media asset to a project. The source file is copied into AppData/PodcastVideoEditor/assets/{projectId}.
        /// Returns the persisted Asset entity.
        /// </summary>
        public async Task<Asset> AddAssetAsync(string projectId, string sourceFilePath, string type = "Image")
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));

            if (string.IsNullOrWhiteSpace(sourceFilePath))
                throw new ArgumentException("File path cannot be empty", nameof(sourceFilePath));

            var fileInfo = new FileInfo(sourceFilePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Asset file not found", sourceFilePath);

            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor", "assets", projectId);
            Directory.CreateDirectory(appData);

            var safeName = Path.GetFileNameWithoutExtension(fileInfo.Name);
            var extension = fileInfo.Extension;
            var destinationFileName = $"{safeName}_{Guid.NewGuid():N}{extension}";
            var destinationPath = Path.Combine(appData, destinationFileName);
            File.Copy(fileInfo.FullName, destinationPath, overwrite: false);

            // Probe audio/video duration for media assets
            double? duration = null;
            var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma", ".ogg" };
            if (audioExtensions.Contains(extension))
            {
                try
                {
                    // Use sample-accurate duration (counts actual decoded samples)
                    // instead of metadata TotalTime which can be wrong for VBR MP3.
                    using var reader = new AudioFileReader(destinationPath);
                    var waveFormat = reader.WaveFormat;
                    long totalSamples = 0;
                    var countBuffer = new float[8192];
                    int countRead;
                    while ((countRead = reader.Read(countBuffer, 0, countBuffer.Length)) > 0)
                        totalSamples += countRead;
                    duration = (double)totalSamples / (waveFormat.SampleRate * waveFormat.Channels);
                    Log.Debug("Probed audio duration for {Name}: {Duration}s (sample-accurate, {Samples} samples)",
                        fileInfo.Name, duration, totalSamples);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to probe audio duration for {Path}", destinationPath);
                }
            }

            var asset = new Asset
            {
                ProjectId = projectId,
                Name = fileInfo.Name,
                Type = type,
                FilePath = destinationPath,
                FileName = fileInfo.Name,
                Extension = extension,
                FileSize = fileInfo.Length,
                Width = null,
                Height = null,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            };

            using var context = _contextFactory.CreateDbContext();
            context.Assets.Add(asset);
            await context.SaveChangesAsync();

            Log.Information("Asset added to project {ProjectId}: {AssetId} ({Name})", projectId, asset.Id, asset.Name);
            return asset;
        }

        /// <summary>
        /// Retrieve an asset by ID.
        /// </summary>
        public async Task<Asset?> GetAssetByIdAsync(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                throw new ArgumentException("Asset ID cannot be empty", nameof(assetId));

            using var context = _contextFactory.CreateDbContext();
            return await context.Assets.FirstOrDefaultAsync(a => a.Id == assetId);
        }

        /// <summary>
        /// Create a new project with default tracks (Text 1, Visual 1).
        /// An Audio track is only created when an audio file is provided at creation time.
        /// Users can add audio tracks at any time by dropping audio assets onto the timeline.
        /// </summary>
        public async Task<Project> CreateProjectAsync(string name, string? audioPath = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project name cannot be empty", nameof(name));

            try
            {
                using var context = _contextFactory.CreateDbContext();

                var project = new Project
                {
                    Name = name,
                    AudioPath = audioPath ?? string.Empty, // keep for backward compat; will be deprecated
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    RenderSettings = new RenderSettings()
                };

                project.Tracks = new List<Track>
                {
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 0, 
                        TrackType = TrackTypes.Text, 
                        TrackRole = TrackRoles.ScriptText,
                        SpanMode = TrackSpanModes.SegmentBound,
                        Name = "Text 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    },
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 1, 
                        TrackType = TrackTypes.Visual, 
                        TrackRole = TrackRoles.AiContent,
                        SpanMode = TrackSpanModes.SegmentBound,
                        Name = "Visual 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    }
                };

                context.Projects.Add(project);
                await context.SaveChangesAsync();

                if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                    await AttachPrimaryAudioAsync(project.Id, audioPath);

                await StretchDynamicVisualOverlaysAsync(project.Id);

                return await GetProjectAsync(project.Id) ?? project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating project");
                throw;
            }
        }

        /// <summary>
        /// Create a new project from a saved template layout (tracks/elements/render settings).
        /// If template is missing or invalid, falls back to a blank project.
        /// </summary>
        public async Task<Project> CreateProjectFromTemplateAsync(string name, string templateId, string? audioPath = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project name cannot be empty", nameof(name));

            if (string.IsNullOrWhiteSpace(templateId) || string.Equals(templateId, "blank", StringComparison.OrdinalIgnoreCase))
                return await CreateProjectAsync(name, audioPath);

            try
            {
                Template? template;
                using (var lookupContext = _contextFactory.CreateDbContext())
                {
                    template = await lookupContext.Templates
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == templateId);
                }

                if (template == null)
                {
                    Log.Warning("Template {TemplateId} not found. Falling back to blank project", templateId);
                    return await CreateProjectAsync(name, audioPath);
                }

                var project = await CreateProjectAsync(name, null);
                var snapshot = DeserializeTemplateSnapshot(template.LayoutJson);

                using (var context = _contextFactory.CreateDbContext())
                {
                    var tracked = await context.Projects
                        .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                        .Include(p => p.Elements)
                        .FirstOrDefaultAsync(p => p.Id == project.Id);

                    if (tracked == null)
                        return await GetProjectAsync(project.Id) ?? project;

                    context.Elements.RemoveRange(tracked.Elements ?? []);
                    context.Tracks.RemoveRange(tracked.Tracks ?? []);
                    await context.SaveChangesAsync();

                    tracked.RenderSettings = snapshot.RenderSettings ?? new RenderSettings();
                    tracked.UpdatedAt = DateTime.UtcNow;

                    var segmentIdMap = new Dictionary<string, string>(StringComparer.Ordinal);

                    if (snapshot.Tracks == null || snapshot.Tracks.Count == 0)
                    {
                        tracked.Tracks = new List<Track>
                        {
                            new Track
                            {
                                ProjectId = tracked.Id,
                                Order = 0,
                                TrackType = TrackTypes.Text,
                                TrackRole = TrackRoles.ScriptText,
                                SpanMode = TrackSpanModes.SegmentBound,
                                Name = "Text 1",
                                IsLocked = false,
                                IsVisible = true,
                            },
                            new Track
                            {
                                ProjectId = tracked.Id,
                                Order = 1,
                                TrackType = TrackTypes.Visual,
                                TrackRole = TrackRoles.AiContent,
                                SpanMode = TrackSpanModes.SegmentBound,
                                Name = "Visual 1",
                                IsLocked = false,
                                IsVisible = true,
                            }
                        };
                    }
                    else
                    {
                        tracked.Tracks = new List<Track>();
                        foreach (var trackSnapshot in snapshot.Tracks.OrderBy(t => t.Order))
                        {
                            var trackType = string.IsNullOrWhiteSpace(trackSnapshot.TrackType)
                                ? TrackTypes.Visual
                                : trackSnapshot.TrackType;

                            var track = new Track
                            {
                                ProjectId = tracked.Id,
                                Order = trackSnapshot.Order,
                                TrackType = trackType,
                                TrackRole = NormalizeTrackRole(trackSnapshot.TrackRole, trackSnapshot.TrackType, trackSnapshot.Name),
                                SpanMode = NormalizeSpanMode(trackSnapshot.SpanMode, trackSnapshot.TrackType, trackSnapshot.Name),
                                Name = string.IsNullOrWhiteSpace(trackSnapshot.Name)
                                    ? "Track"
                                    : trackSnapshot.Name,
                                IsLocked = trackSnapshot.IsLocked,
                                IsVisible = trackSnapshot.IsVisible,
                                ImageLayoutPreset = string.IsNullOrWhiteSpace(trackSnapshot.ImageLayoutPreset)
                                    ? ImageLayoutPresets.FullFrame
                                    : trackSnapshot.ImageLayoutPreset,
                                AutoMotionEnabled = trackSnapshot.AutoMotionEnabled,
                                MotionIntensity = trackSnapshot.MotionIntensity,
                                OverlayColorHex = string.IsNullOrWhiteSpace(trackSnapshot.OverlayColorHex)
                                    ? "#000000"
                                    : trackSnapshot.OverlayColorHex,
                                OverlayOpacity = trackSnapshot.OverlayOpacity,
                                TextStyleJson = trackSnapshot.TextStyleJson,
                            };

                            track.Segments = new List<Segment>();
                            foreach (var segmentSnapshot in (trackSnapshot.Segments ?? []).OrderBy(s => s.Order))
                            {
                                // Template snapshots may contain audio blocks without a valid asset binding.
                                // Those segments are not playable in timeline preview and should not be materialized.
                                if (string.Equals(trackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase)
                                    && string.IsNullOrWhiteSpace(segmentSnapshot.BackgroundAssetId))
                                {
                                    continue;
                                }

                                var segment = new Segment
                                {
                                    ProjectId = tracked.Id,
                                    TrackId = track.Id,
                                    StartTime = segmentSnapshot.StartTime,
                                    EndTime = segmentSnapshot.EndTime,
                                    Text = segmentSnapshot.Text ?? string.Empty,
                                    BackgroundAssetId = segmentSnapshot.BackgroundAssetId,
                                    TransitionType = string.IsNullOrWhiteSpace(segmentSnapshot.TransitionType)
                                        ? "fade"
                                        : segmentSnapshot.TransitionType,
                                    TransitionDuration = segmentSnapshot.TransitionDuration,
                                    Order = segmentSnapshot.Order,
                                    Kind = string.IsNullOrWhiteSpace(segmentSnapshot.Kind)
                                        ? SegmentKinds.Visual
                                        : segmentSnapshot.Kind,
                                    Keywords = segmentSnapshot.Keywords,
                                    Volume = segmentSnapshot.Volume,
                                    FadeInDuration = segmentSnapshot.FadeInDuration,
                                    FadeOutDuration = segmentSnapshot.FadeOutDuration,
                                    SourceStartOffset = segmentSnapshot.SourceStartOffset,
                                    MotionPreset = string.IsNullOrWhiteSpace(segmentSnapshot.MotionPreset)
                                        ? MotionPresets.None
                                        : segmentSnapshot.MotionPreset,
                                    MotionIntensity = segmentSnapshot.MotionIntensity,
                                    OverlayColorHex = segmentSnapshot.OverlayColorHex,
                                    OverlayOpacity = segmentSnapshot.OverlayOpacity,
                                };

                                track.Segments.Add(segment);
                                if (!string.IsNullOrWhiteSpace(segmentSnapshot.Id))
                                    segmentIdMap[segmentSnapshot.Id] = segment.Id;
                            }

                            tracked.Tracks.Add(track);
                        }
                    }

                    tracked.Elements = new List<Element>();
                    foreach (var elementSnapshot in snapshot.Elements ?? [])
                    {
                        string? mappedSegmentId = null;
                        if (!string.IsNullOrWhiteSpace(elementSnapshot.SegmentId))
                            segmentIdMap.TryGetValue(elementSnapshot.SegmentId, out mappedSegmentId);

                        tracked.Elements.Add(new Element
                        {
                            ProjectId = tracked.Id,
                            Type = string.IsNullOrWhiteSpace(elementSnapshot.Type)
                                ? "TextOverlay"
                                : elementSnapshot.Type,
                            X = elementSnapshot.X,
                            Y = elementSnapshot.Y,
                            Width = elementSnapshot.Width,
                            Height = elementSnapshot.Height,
                            Rotation = elementSnapshot.Rotation,
                            ZIndex = elementSnapshot.ZIndex,
                            Opacity = elementSnapshot.Opacity,
                            PropertiesJson = string.IsNullOrWhiteSpace(elementSnapshot.PropertiesJson)
                                ? "{}"
                                : elementSnapshot.PropertiesJson,
                            IsVisible = elementSnapshot.IsVisible,
                            SegmentId = mappedSegmentId,
                        });
                    }

                    var trackedTemplate = await context.Templates.FirstOrDefaultAsync(t => t.Id == template.Id);
                    if (trackedTemplate != null)
                        trackedTemplate.LastUsedAt = DateTime.UtcNow;

                    await context.SaveChangesAsync();
                }

                if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                    await AttachPrimaryAudioAsync(project.Id, audioPath);

                return await GetProjectAsync(project.Id) ?? project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating project from template {TemplateId}", templateId);
                throw;
            }
        }

        private async Task AttachPrimaryAudioAsync(string projectId, string audioPath)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                var project = await context.Projects
                    .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                    .Include(p => p.Assets)
                    .FirstOrDefaultAsync(p => p.Id == projectId);

                if (project == null)
                    return;

                var audioTrack = project.Tracks
                    .OrderBy(t => t.Order)
                    .FirstOrDefault(t => string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));

                if (audioTrack == null)
                {
                    var maxOrder = project.Tracks.Any() ? project.Tracks.Max(t => t.Order) : 1;
                    audioTrack = new Track
                    {
                        ProjectId = project.Id,
                        Order = maxOrder + 1,
                        TrackType = TrackTypes.Audio,
                        TrackRole = TrackRoles.Unspecified,
                        SpanMode = TrackSpanModes.SegmentBound,
                        Name = "Audio 1",
                        IsLocked = false,
                        IsVisible = true,
                    };
                    project.Tracks.Add(audioTrack);
                    context.Tracks.Add(audioTrack);
                    await context.SaveChangesAsync();
                }

                var normalizedAudioPath = NormalizeFilePath(audioPath);
                var sourceAudioFileName = Path.GetFileName(audioPath);
                bool alreadyOnTimeline = project.Tracks
                    .Where(t => string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(t => t.Segments ?? [])
                    .Any(s =>
                    {
                        if (string.IsNullOrWhiteSpace(s.BackgroundAssetId))
                            return false;

                        var segAsset = project.Assets?.FirstOrDefault(a => a.Id == s.BackgroundAssetId);
                        if (segAsset == null || string.IsNullOrWhiteSpace(segAsset.FilePath))
                            return false;

                        return string.Equals(NormalizeFilePath(segAsset.FilePath), normalizedAudioPath, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(segAsset.Name, sourceAudioFileName, StringComparison.OrdinalIgnoreCase);
                    });

                if (!alreadyOnTimeline)
                {
                    var importedAsset = await AddAssetAsync(project.Id, audioPath, "Audio");
                    var segmentDuration = importedAsset.Duration ?? 0;

                    if (segmentDuration <= 0)
                    {
                        try
                        {
                            using var reader = new AudioFileReader(importedAsset.FilePath);
                            segmentDuration = Math.Max(0, reader.TotalTime.TotalSeconds);
                        }
                        catch
                        {
                            segmentDuration = 600;
                        }
                    }

                    audioTrack.Segments ??= [];
                    var nextOrder = audioTrack.Segments.Any() ? audioTrack.Segments.Max(s => s.Order) + 1 : 0;
                    var segment = new Segment
                    {
                        ProjectId = project.Id,
                        TrackId = audioTrack.Id,
                        StartTime = 0,
                        EndTime = segmentDuration,
                        Text = Path.GetFileNameWithoutExtension(audioPath),
                        BackgroundAssetId = importedAsset.Id,
                        Kind = SegmentKinds.Audio,
                        Volume = 1.0,
                        FadeInDuration = 0,
                        FadeOutDuration = 0,
                        Order = nextOrder,
                    };

                    audioTrack.Segments.Add(segment);
                    context.Segments.Add(segment);
                }

                project.AudioPath = audioPath;
                project.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();

                Log.Information("Primary audio attached to project {ProjectId}", projectId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not attach primary audio for project {ProjectId}", projectId);
            }
        }

        private static TemplateProjectSnapshot DeserializeTemplateSnapshot(string layoutJson)
        {
            if (string.IsNullOrWhiteSpace(layoutJson))
                return new TemplateProjectSnapshot();

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                };

                var snapshot = JsonSerializer.Deserialize<TemplateProjectSnapshot>(layoutJson, options);
                if (snapshot != null)
                {
                    snapshot.Tracks ??= [];
                    snapshot.Elements ??= [];
                    foreach (var track in snapshot.Tracks)
                        track.Segments ??= [];
                }
                return snapshot ?? new TemplateProjectSnapshot();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to deserialize template layout JSON. Falling back to default tracks");
                return new TemplateProjectSnapshot();
            }
        }

        private sealed class TemplateProjectSnapshot
        {
            public RenderSettings? RenderSettings { get; set; }
            public List<TemplateTrackSnapshot> Tracks { get; set; } = [];
            public List<TemplateElementSnapshot> Elements { get; set; } = [];
        }

        private sealed class TemplateTrackSnapshot
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

        private sealed class TemplateSegmentSnapshot
        {
            public string? Id { get; set; }
            public double StartTime { get; set; }
            public double EndTime { get; set; }
            public string? Text { get; set; }
            public string? BackgroundAssetId { get; set; }
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

        private sealed class TemplateElementSnapshot
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

        /// <summary>
        /// Retrieve a project with all related data (tracks, segments, elements, assets, BGM tracks).
        /// </summary>
        public async Task<Project?> GetProjectAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));

            try
            {
                using var context = _contextFactory.CreateDbContext();
                return await context.Projects
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
                    .Include(p => p.Elements)
                    .Include(p => p.Assets)
                    .Include(p => p.BgmTracks)
                    .FirstOrDefaultAsync(p => p.Id == projectId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving project {ProjectId}", projectId);
                throw;
            }
        }

        /// <summary>
        /// Migrate legacy Project.AudioPath → audio segment on the Audio track.
        /// If the project has an AudioPath set but no audio segments on the default Audio track,
        /// import the audio file as an Asset and create a full-duration audio segment.
        /// Also migrates BgmTracks to audio segments on a dedicated BGM audio track.
        /// Returns true if any migration was performed (caller should reload project).
        /// </summary>
        public async Task<bool> MigrateToAudioSegmentsAsync(Project project)
        {
            if (project == null) return false;

            bool migrated = false;

            // ── 1. Migrate Project.AudioPath → audio segment (timeline-first) ─
            if (!string.IsNullOrWhiteSpace(project.AudioPath) && File.Exists(project.AudioPath))
            {
                using var primaryAudioContext = _contextFactory.CreateDbContext();
                var trackedProject = await primaryAudioContext.Projects
                    .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                    .Include(p => p.Assets)
                    .FirstOrDefaultAsync(p => p.Id == project.Id);

                if (trackedProject != null)
                {
                    var normalizedAudioPath = NormalizeFilePath(project.AudioPath);
                    var sourceAudioFileName = Path.GetFileName(project.AudioPath);
                    var hasMatchingAudioSegment = trackedProject.Tracks
                        .Where(t => string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(t => t.Segments ?? [])
                        .Any(s =>
                        {
                            if (string.IsNullOrWhiteSpace(s.BackgroundAssetId))
                                return false;

                            var segAsset = trackedProject.Assets?.FirstOrDefault(a => a.Id == s.BackgroundAssetId);
                            if (segAsset == null || string.IsNullOrWhiteSpace(segAsset.FilePath))
                                return false;

                            return string.Equals(NormalizeFilePath(segAsset.FilePath), normalizedAudioPath, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(segAsset.Name, sourceAudioFileName, StringComparison.OrdinalIgnoreCase);
                        });

                    if (!hasMatchingAudioSegment)
                    {
                        var audioTrack = trackedProject.Tracks
                            .OrderBy(t => t.Order)
                            .FirstOrDefault(t => string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));

                        if (audioTrack == null)
                        {
                            var maxOrder = trackedProject.Tracks.Any() ? trackedProject.Tracks.Max(t => t.Order) : 1;
                            audioTrack = new Track
                            {
                                ProjectId = trackedProject.Id,
                                Order = maxOrder + 1,
                                TrackType = TrackTypes.Audio,
                                TrackRole = TrackRoles.Unspecified,
                                SpanMode = TrackSpanModes.SegmentBound,
                                Name = "Audio 1",
                                IsLocked = false,
                                IsVisible = true,
                            };
                            trackedProject.Tracks.Add(audioTrack);
                            primaryAudioContext.Tracks.Add(audioTrack);
                            await primaryAudioContext.SaveChangesAsync();
                        }

                        var asset = await AddAssetAsync(trackedProject.Id, project.AudioPath, "Audio");
                        var duration = asset.Duration ?? 600;
                        audioTrack.Segments ??= [];
                        var nextOrder = audioTrack.Segments.Any() ? audioTrack.Segments.Max(s => s.Order) + 1 : 0;
                        var segment = new Segment
                        {
                            ProjectId = trackedProject.Id,
                            TrackId = audioTrack.Id,
                            StartTime = 0,
                            EndTime = duration,
                            Text = Path.GetFileNameWithoutExtension(project.AudioPath),
                            BackgroundAssetId = asset.Id,
                            Kind = SegmentKinds.Audio,
                            Volume = 1.0,
                            FadeInDuration = 0,
                            FadeOutDuration = 0,
                            Order = nextOrder,
                        };

                        audioTrack.Segments.Add(segment);
                        primaryAudioContext.Segments.Add(segment);
                        trackedProject.UpdatedAt = DateTime.UtcNow;
                        await primaryAudioContext.SaveChangesAsync();
                        migrated = true;
                        Log.Information("Migrated Project.AudioPath → audio segment for project {ProjectId}", trackedProject.Id);
                    }
                }
            }

            // ── 2. Migrate BgmTracks → audio segments ───────────────────────
            if (project.BgmTracks?.Count > 0)
            {
                using var bgmContext = _contextFactory.CreateDbContext();
                var trackedProject = await bgmContext.Projects
                    .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                    .Include(p => p.Assets)
                    .Include(p => p.BgmTracks)
                    .FirstOrDefaultAsync(p => p.Id == project.Id);

                if (trackedProject != null)
                {
                    foreach (var bgm in trackedProject.BgmTracks.ToList())
                    {
                        if (string.IsNullOrWhiteSpace(bgm.AudioPath) || !File.Exists(bgm.AudioPath))
                            continue;

                        // Find or create a dedicated BGM audio track
                        var bgmTrack = trackedProject.Tracks?.FirstOrDefault(t =>
                            string.Equals(t.Name, "BGM", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));

                        if (bgmTrack == null)
                        {
                            int maxOrder = trackedProject.Tracks?.Max(t => t.Order) ?? 2;
                            bgmTrack = new Track
                            {
                                ProjectId = trackedProject.Id,
                                Order = maxOrder + 1,
                                TrackType = TrackTypes.Audio,
                                TrackRole = TrackRoles.Unspecified,
                                SpanMode = TrackSpanModes.SegmentBound,
                                Name = "BGM",
                                IsLocked = false,
                                IsVisible = true
                            };
                            trackedProject.Tracks ??= new List<Track>();
                            trackedProject.Tracks.Add(bgmTrack);
                            bgmContext.Tracks.Add(bgmTrack);
                            await bgmContext.SaveChangesAsync(); // need ID for segment FK
                        }

                        // Check not already migrated
                        bool alreadyHasBgmSeg = bgmTrack.Segments?.Any(s =>
                            !string.IsNullOrWhiteSpace(s.BackgroundAssetId)) == true;

                        if (!alreadyHasBgmSeg)
                        {
                            try
                            {
                                var asset = await AddAssetAsync(trackedProject.Id, bgm.AudioPath, "Audio");
                                double duration = asset.Duration ?? 600;

                                // Use full project duration for BGM (mirrors old looping behavior)
                                var projectDuration = trackedProject.Tracks?
                                    .SelectMany(t => t.Segments ?? Enumerable.Empty<Segment>())
                                    .Where(s => s.EndTime > 0)
                                    .Select(s => s.EndTime)
                                    .DefaultIfEmpty(duration)
                                    .Max() ?? duration;

                                var segment = new Segment
                                {
                                    ProjectId = trackedProject.Id,
                                    TrackId = bgmTrack.Id,
                                    StartTime = bgm.StartTime,
                                    EndTime = bgm.IsLooping ? projectDuration : Math.Min(bgm.StartTime + duration, projectDuration),
                                    Text = bgm.Name ?? "BGM",
                                    BackgroundAssetId = asset.Id,
                                    Kind = SegmentKinds.Audio,
                                    Volume = bgm.Volume,
                                    FadeInDuration = bgm.FadeInSeconds,
                                    FadeOutDuration = bgm.FadeOutSeconds,
                                    Order = 0
                                };

                                bgmTrack.Segments ??= new List<Segment>();
                                bgmTrack.Segments.Add(segment);
                                bgmContext.Segments.Add(segment);

                                // Remove old BgmTrack record
                                bgmContext.BgmTracks.Remove(bgm);

                                migrated = true;
                                Log.Information("Migrated BgmTrack → audio segment for project {ProjectId}", trackedProject.Id);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Failed to migrate BgmTrack to segment for project {ProjectId}", trackedProject.Id);
                            }
                        }
                    }
                }

                if (migrated)
                    await bgmContext.SaveChangesAsync();
            }

            return migrated;
        }

        private static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        /// <summary>
        /// Replace segments of a specific track with a new list (used for script apply).
        /// Only segments in the specified track are affected; other tracks remain unchanged.
        /// </summary>
        public async Task ReplaceSegmentsOfTrackAsync(Project project, string trackId, IEnumerable<Segment> newSegments)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            if (string.IsNullOrWhiteSpace(trackId))
                throw new ArgumentException("Track ID cannot be empty", nameof(trackId));

            try
            {
                using var context = _contextFactory.CreateDbContext();
                var track = await context.Tracks.FindAsync(trackId);
                if (track == null)
                    throw new KeyNotFoundException($"Track {trackId} not found");

                if (track.ProjectId != project.Id)
                    throw new InvalidOperationException($"Track {trackId} does not belong to project {project.Id}");

                var list = newSegments.ToList();

                // Set ProjectId and TrackId for all new segments
                foreach (var s in list)
                {
                    s.ProjectId = project.Id;
                    s.TrackId = trackId;
                }

                // Remove old segments from this track
                var oldSegments = context.Segments.Where(s => s.TrackId == trackId).ToList();
                foreach (var s in oldSegments)
                    context.Segments.Remove(s);

                // Add new segments
                foreach (var s in list)
                    context.Segments.Add(s);

                project.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();

                Log.Information("Replaced segments for track {TrackId}: {Count} segments", trackId, list.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error replacing segments for track {TrackId}", trackId);
                throw;
            }
        }

        /// <summary>
        /// Get all tracks for a project.
        /// </summary>
        public async Task<List<Track>> GetTracksAsync(string projectId)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                return await context.Tracks
                    .Where(t => t.ProjectId == projectId)
                    .Include(t => t.Segments)
                    .OrderBy(t => t.Order)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving tracks for project {ProjectId}", projectId);
                throw;
            }
        }

        /// <summary>
        /// Get a specific track by ID.
        /// </summary>
        public async Task<Track?> GetTrackByIdAsync(string trackId)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                return await context.Tracks
                    .Include(t => t.Segments)
                    .FirstOrDefaultAsync(t => t.Id == trackId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving track {TrackId}", trackId);
                throw;
            }
        }

        /// <summary>
        /// Update a track.
        /// </summary>
        public async Task<Track> UpdateTrackAsync(Track track)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));

            try
            {
                using var context = _contextFactory.CreateDbContext();
                context.Tracks.Update(track);
                await context.SaveChangesAsync();

                Log.Information("Track updated: {TrackId}", track.Id);
                return track;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating track {TrackId}", track.Id);
                throw;
            }
        }

        /// <summary>
        /// Add an element to a project. Use SegmentId to attach the element to a segment (optional).
        /// </summary>
        public async Task<Element> AddElementAsync(string projectId, Element element)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            element.ProjectId = projectId;
            using var context = _contextFactory.CreateDbContext();
            context.Elements.Add(element);
            await context.SaveChangesAsync();
            Log.Information("Element added to project {ProjectId}: {ElementId} (SegmentId: {SegmentId})", projectId, element.Id, element.SegmentId ?? "null");
            return element;
        }

        /// <summary>
        /// Update an existing element (e.g. SegmentId, X, Y, Width, Height, PropertiesJson).
        /// </summary>
        public async Task<Element> UpdateElementAsync(Element element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            using var context = _contextFactory.CreateDbContext();
            context.Elements.Update(element);
            await context.SaveChangesAsync();
            Log.Information("Element updated: {ElementId}", element.Id);
            return element;
        }

        /// <summary>
        /// Delete a track by ID. All segments in the track are also deleted (cascade).
        /// </summary>
        public async Task DeleteTrackAsync(string trackId)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                var deleted = await context.Tracks
                    .Where(t => t.Id == trackId)
                    .ExecuteDeleteAsync();

                if (deleted == 0)
                    Log.Warning("DeleteTrackAsync: Track {TrackId} not found in database (may have been deleted already)", trackId);
                else
                    Log.Information("Track deleted: {TrackId}", trackId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting track {TrackId}", trackId);
                throw;
            }
        }

        /// <summary>
        /// Extend dynamic visual overlay tracks (logo/icon/watermark-like) so their single segment
        /// always spans the current project duration.
        /// A dynamic overlay track is identified heuristically as:
        /// - visual track
        /// - exactly one segment starting near 0
        /// - and track is locked OR its name contains branding keywords (logo/icon/overlay/watermark/brand)
        /// </summary>
        public async Task<int> StretchDynamicVisualOverlaysAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return 0;

            try
            {
                using var context = _contextFactory.CreateDbContext();
                var project = await context.Projects
                    .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                    .FirstOrDefaultAsync(p => p.Id == projectId);

                if (project == null || project.Tracks == null || project.Tracks.Count == 0)
                    return 0;

                var allSegments = project.Tracks
                    .SelectMany(t => t.Segments ?? Enumerable.Empty<Segment>())
                    .Where(s => s.EndTime > 0)
                    .ToList();

                if (allSegments.Count == 0)
                    return 0;

                var targetEnd = allSegments.Max(s => s.EndTime);
                if (targetEnd <= 0)
                    return 0;

                int changed = 0;
                foreach (var track in project.Tracks)
                {
                    if (!ShouldAutoStretchToProjectDuration(track))
                        continue;

                    var seg = track.Segments!.Single();
                    if (Math.Abs(seg.EndTime - targetEnd) <= 0.01)
                        continue;

                    seg.StartTime = 0;
                    seg.EndTime = targetEnd;
                    changed++;
                }

                if (changed > 0)
                {
                    project.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();
                    Log.Information("Stretched {Count} dynamic overlay track(s) to {Duration:F3}s for project {ProjectId}",
                        changed, targetEnd, projectId);
                }

                return changed;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stretching dynamic overlays for project {ProjectId}", projectId);
                throw;
            }
        }

        private static bool IsDynamicOverlayTrack(Track track)
        {
            if (!string.Equals(track.TrackType, TrackTypes.Visual, StringComparison.OrdinalIgnoreCase))
                return false;

            var segs = (track.Segments ?? []).Where(s => s.EndTime > s.StartTime).ToList();
            if (segs.Count != 1)
                return false;

            var seg = segs[0];
            if (seg.StartTime > 0.05)
                return false;

            if (string.IsNullOrWhiteSpace(seg.BackgroundAssetId))
                return false;

            if (track.IsLocked)
                return true;

            var name = track.Name ?? string.Empty;
            return name.Contains("logo", StringComparison.OrdinalIgnoreCase)
                || name.Contains("icon", StringComparison.OrdinalIgnoreCase)
                || name.Contains("overlay", StringComparison.OrdinalIgnoreCase)
                || name.Contains("watermark", StringComparison.OrdinalIgnoreCase)
                || name.Contains("brand", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldAutoStretchToProjectDuration(Track track)
        {
            // New policy path: explicit span mode from track metadata.
            if (string.Equals(track.SpanMode, TrackSpanModes.ProjectDuration, StringComparison.OrdinalIgnoreCase))
                return true;

            // Legacy compatibility path.
            return IsDynamicOverlayTrack(track);
        }

        private static string NormalizeTrackRole(string? role, string? trackType, string? name)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                var normalizedType = trackType?.Trim().ToLowerInvariant() ?? string.Empty;
                var normalizedName = name?.Trim() ?? string.Empty;

                if (normalizedType == TrackTypes.Text)
                    return TrackRoles.ScriptText;

                if (normalizedType == TrackTypes.Visual)
                {
                    if (normalizedName.Contains("logo", StringComparison.OrdinalIgnoreCase)
                        || normalizedName.Contains("icon", StringComparison.OrdinalIgnoreCase)
                        || normalizedName.Contains("watermark", StringComparison.OrdinalIgnoreCase)
                        || normalizedName.Contains("brand", StringComparison.OrdinalIgnoreCase))
                        return TrackRoles.BrandOverlay;

                    if (normalizedName.Contains("visualizer", StringComparison.OrdinalIgnoreCase)
                        || normalizedName.Contains("spectrum", StringComparison.OrdinalIgnoreCase))
                        return TrackRoles.Visualizer;

                    return TrackRoles.AiContent;
                }

                return TrackRoles.Unspecified;
            }

            return role.Trim().ToLowerInvariant();
        }

        private static string NormalizeSpanMode(string? spanMode, string? trackType, string? name)
        {
            if (string.IsNullOrWhiteSpace(spanMode))
            {
                var inferredRole = NormalizeTrackRole(null, trackType, name);
                return inferredRole switch
                {
                    TrackRoles.BrandOverlay => TrackSpanModes.ProjectDuration,
                    TrackRoles.TitleOverlay => TrackSpanModes.ProjectDuration,
                    TrackRoles.Visualizer => TrackSpanModes.ProjectDuration,
                    _ => TrackSpanModes.SegmentBound,
                };
            }

            var value = spanMode.Trim().ToLowerInvariant();
            return value switch
            {
                TrackSpanModes.ProjectDuration => TrackSpanModes.ProjectDuration,
                TrackSpanModes.TemplateDuration => TrackSpanModes.TemplateDuration,
                TrackSpanModes.SegmentBound => TrackSpanModes.SegmentBound,
                TrackSpanModes.Manual => TrackSpanModes.Manual,
                _ => TrackSpanModes.SegmentBound,
            };
        }

        /// <summary>
        /// Replace all elements of a project with a new list (for canvas save).
        /// Uses ExecuteDeleteAsync (direct SQL) for deletion to avoid EF change-tracker
        /// conflicts with stale tracked instances from prior saves or failed operations.
        /// </summary>
        public async Task ReplaceElementsAsync(string projectId, IEnumerable<Element> newElements)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));

            try
            {
                using var context = _contextFactory.CreateDbContext();

                // Delete existing elements via direct SQL.
                await context.Elements
                    .Where(e => e.ProjectId == projectId)
                    .ExecuteDeleteAsync();

                var elementList = newElements.ToList();
                foreach (var el in elementList)
                {
                    el.ProjectId = projectId;
                    context.Elements.Add(el);
                }

                await context.SaveChangesAsync();
                Log.Information("Replaced elements for project {ProjectId}: {Count} elements", projectId, elementList.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error replacing elements for project {ProjectId}", projectId);
                throw;
            }
        }

        /// <summary>
        /// Update project.
        /// </summary>
        public async Task<Project> UpdateProjectAsync(Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            try
            {
                project.UpdatedAt = DateTime.UtcNow;

                using var context = _contextFactory.CreateDbContext();

                // Determine which entities already exist in DB BEFORE attaching the
                // object graph. Projects.Update() marks every reachable entity as
                // Modified; new rows (not yet in DB) must be re-marked as Added.
                var existingBgmIds = project.BgmTracks?.Count > 0
                    ? new HashSet<string>(
                          await context.BgmTracks
                              .Where(b => b.ProjectId == project.Id)
                              .Select(b => b.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                var existingTrackIds = project.Tracks?.Count > 0
                    ? new HashSet<string>(
                          await context.Tracks
                              .Where(t => t.ProjectId == project.Id)
                              .Select(t => t.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                var existingSegmentIds = project.Tracks?.Any(t => t.Segments?.Count > 0) == true
                    ? new HashSet<string>(
                          await context.Segments
                              .Where(s => s.ProjectId == project.Id)
                              .Select(s => s.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                // Elements are fully managed by ReplaceElementsAsync / SaveElementsAsync.
                // Do NOT query, orphan-delete, or FixNew for elements here.

                // Compute orphan IDs BEFORE attaching the graph — orphans are entities
                // that exist in the DB but were removed from the in-memory collections.
                var orphanBgmIds = ComputeOrphanIds(existingBgmIds, project.BgmTracks?.Select(b => b.Id));
                var orphanTrackIds = ComputeOrphanIds(existingTrackIds, project.Tracks?.Select(t => t.Id));
                var orphanSegmentIds = ComputeOrphanIds(existingSegmentIds,
                    project.Tracks?.SelectMany(t => t.Segments ?? Enumerable.Empty<Segment>()).Select(s => s.Id));

                // Exclude project.Elements from the Update() graph walk.
                ICollection<Element> elementsSnapshot = project.Elements;
                project.Elements = [];

                Exception? saveError = null;
                try
                {
                    context.Projects.Update(project);

                    // Fix up any brand-new entities that were incorrectly
                    // stamped Modified by the graph walk above.
                    FixNewEntities(context, project.BgmTracks, existingBgmIds, b => b.Id);
                    FixNewEntities(context, project.Tracks, existingTrackIds, t => t.Id);
                    if (project.Tracks != null)
                        foreach (var track in project.Tracks)
                            FixNewEntities(context, track.Segments, existingSegmentIds, s => s.Id);

                    await context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    saveError = ex;
                }
                finally
                {
                    // Restore the in-memory collection so callers still see the full project.
                    // Must be AFTER SaveChangesAsync — if restored before, EF Core's DetectChanges
                    // re-discovers the elements in the navigation and tries to INSERT them,
                    // causing UNIQUE constraint failures.
                    project.Elements = elementsSnapshot;
                }

                // Delete orphaned entities via direct SQL regardless of SaveChangesAsync
                // outcome. ExecuteDeleteAsync bypasses the change tracker, so it works
                // even when SaveChangesAsync failed. This ensures deleted segments are
                // removed from the DB and won't reappear on next project load.
                if (orphanSegmentIds.Count > 0)
                {
                    await context.Segments
                        .Where(s => orphanSegmentIds.Contains(s.Id))
                        .ExecuteDeleteAsync();
                    Log.Debug("Deleted {Count} orphaned segment(s)", orphanSegmentIds.Count);
                }
                if (orphanTrackIds.Count > 0)
                {
                    await context.Tracks
                        .Where(t => orphanTrackIds.Contains(t.Id))
                        .ExecuteDeleteAsync();
                    Log.Debug("Deleted {Count} orphaned track(s)", orphanTrackIds.Count);
                }
                if (orphanBgmIds.Count > 0)
                {
                    await context.BgmTracks
                        .Where(b => orphanBgmIds.Contains(b.Id))
                        .ExecuteDeleteAsync();
                    Log.Debug("Deleted {Count} orphaned BGM track(s)", orphanBgmIds.Count);
                }

                Log.Information("Project updated: {ProjectId}", project.Id);

                // If SaveChangesAsync failed, re-throw after orphan cleanup completed.
                if (saveError != null)
                    throw saveError;

                return project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating project {ProjectId}", project.Id);
                throw;
            }
        }

        /// <summary>
        /// Compute the set of IDs that exist in the DB but are no longer in the in-memory collection.
        /// </summary>
        private static List<string> ComputeOrphanIds(
            HashSet<string> existingDbIds,
            IEnumerable<string>? currentIds)
        {
            var keepIds = currentIds != null
                ? new HashSet<string>(currentIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var orphans = new List<string>();
            foreach (var id in existingDbIds)
            {
                if (!keepIds.Contains(id))
                    orphans.Add(id);
            }
            return orphans;
        }

        /// <summary>
        /// Mark entities as <see cref="EntityState.Added"/> if their ID is not in <paramref name="existingDbIds"/>.
        /// </summary>
        private static void FixNewEntities<T>(DbContext context, IEnumerable<T>? entities, HashSet<string> existingDbIds, Func<T, string> idSelector) where T : class
        {
            if (entities == null) return;
            foreach (var entity in entities)
                if (!existingDbIds.Contains(idSelector(entity)))
                    context.Entry(entity).State = EntityState.Added;
        }

        /// <summary>
        /// Delete project by ID.
        /// Also deletes all asset files on disk under AppData/PodcastVideoEditor/assets/{projectId}.
        /// </summary>
        public async Task DeleteProjectAsync(string projectId)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                var project = await context.Projects.FindAsync(projectId);
                if (project == null)
                    throw new KeyNotFoundException($"Project {projectId} not found");

                context.Projects.Remove(project);
                await context.SaveChangesAsync();

                // Delete asset files from disk after DB removal succeeds
                var assetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PodcastVideoEditor", "assets", projectId);
                if (Directory.Exists(assetDir))
                {
                    try
                    {
                        Directory.Delete(assetDir, recursive: true);
                        Log.Information("Deleted asset folder for project {ProjectId}: {Path}", projectId, assetDir);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not delete asset folder for project {ProjectId}: {Path}", projectId, assetDir);
                    }
                }

                Log.Information("Project deleted: {ProjectId}", projectId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting project {ProjectId}", projectId);
                throw;
            }
        }

        /// <summary>
        /// Deletes project assets (DB records + disk files) that are no longer referenced
        /// by any visual segment. Safe to call after AI re-analysis.
        /// Returns the number of assets purged.
        /// </summary>
        public async Task<int> PurgeUnusedAssetsAsync(string projectId)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();

                // Collect asset IDs referenced by any segment in this project
                var referencedAssetIds = await context.Tracks
                    .Where(t => t.ProjectId == projectId)
                    .SelectMany(t => t.Segments)
                    .Where(s => s.BackgroundAssetId != null)
                    .Select(s => s.BackgroundAssetId!)
                    .Distinct()
                    .ToListAsync();

                // Find assets in this project that are not referenced
                var unusedAssets = await context.Assets
                    .Where(a => a.ProjectId == projectId && !referencedAssetIds.Contains(a.Id))
                    .ToListAsync();

                if (unusedAssets.Count == 0)
                    return 0;

                // Delete disk files first (best-effort)
                foreach (var asset in unusedAssets)
                {
                    if (!string.IsNullOrWhiteSpace(asset.FilePath) && File.Exists(asset.FilePath))
                    {
                        try { File.Delete(asset.FilePath); }
                        catch (Exception ex) { Log.Warning(ex, "Could not delete asset file {Path}", asset.FilePath); }
                    }
                }

                // Remove DB records
                context.Assets.RemoveRange(unusedAssets);
                await context.SaveChangesAsync();

                Log.Information("Purged {Count} unused asset(s) for project {ProjectId}", unusedAssets.Count, projectId);
                return unusedAssets.Count;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error purging unused assets for project {ProjectId}", projectId);
                return 0;
            }
        }

        /// <summary>
        /// Get recent projects (last 5).
        /// Includes tracks and their segments - essential for timeline display.
        /// Assets, Elements, and BGM are loaded on-demand when project is opened.
        /// </summary>
        public async Task<List<Project>> GetAllProjectsAsync()
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                return await context.Projects
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
                    .OrderByDescending(p => p.UpdatedAt)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving all projects");
                throw;
            }
        }

        /// <summary>
        /// Get the N most recently updated projects, with tracks, segments, and assets populated.
        /// Elements and BGM are loaded on-demand when project is opened.
        /// Assets are included so that waveform peak loading can resolve audio file
        /// paths even if CurrentProject is replaced from this list query.
        /// </summary>
        public async Task<List<Project>> GetRecentProjectsAsync(int count = 5)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                return await context.Projects
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
                    .Include(p => p.Assets)
                    .OrderByDescending(p => p.UpdatedAt)
                    .Take(count)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving recent projects");
                throw;
            }
        }
    }
}
