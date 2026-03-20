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
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Service for project-specific CRUD operations.
    /// </summary>
    public class ProjectService
    {
        private readonly AppDbContext _context;

        public ProjectService(AppDbContext context)
        {
            _context = context;
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
                    using var reader = new AudioFileReader(destinationPath);
                    duration = reader.TotalTime.TotalSeconds;
                    Log.Debug("Probed audio duration for {Name}: {Duration}s", fileInfo.Name, duration);
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

            _context.Assets.Add(asset);
            await _context.SaveChangesAsync();

            Log.Information("Asset added to project {ProjectId}: {AssetId} ({Name})", projectId, asset.Id, asset.Name);
            return asset;
        }

        /// <summary>
        /// Retrieve an asset by ID.
        /// </summary>
        public Task<Asset?> GetAssetByIdAsync(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
                throw new ArgumentException("Asset ID cannot be empty", nameof(assetId));

            return _context.Assets.FirstOrDefaultAsync(a => a.Id == assetId);
        }

        /// <summary>
        /// Create a new project with default tracks (Text 1, Visual 1, Audio).
        /// Audio path is optional — when provided the audio file is imported as an
        /// Asset and placed as the first segment on the default Audio track.
        /// </summary>
        public async Task<Project> CreateProjectAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project name cannot be empty", nameof(name));

            try
            {
                var project = new Project
                {
                    Name = name,
                    AudioPath = audioPath ?? string.Empty, // keep for backward compat; will be deprecated
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    RenderSettings = new RenderSettings()
                };

                // Create default tracks for the project
                var audioTrack = new Track
                {
                    ProjectId = project.Id,
                    Order = 2,
                    TrackType = TrackTypes.Audio,
                    Name = "Audio",
                    IsLocked = false,
                    IsVisible = true
                };

                project.Tracks = new List<Track>
                {
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 0, 
                        TrackType = TrackTypes.Text, 
                        Name = "Text 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    },
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 1, 
                        TrackType = TrackTypes.Visual, 
                        Name = "Visual 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    },
                    audioTrack
                };

                _context.Projects.Add(project);
                await _context.SaveChangesAsync();

                // If an audio file was provided, import it as an Asset and place
                // a full-duration audio segment on the default Audio track.
                if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                {
                    try
                    {
                        var asset = await AddAssetAsync(project.Id, audioPath, "Audio");
                        var duration = asset.Duration ?? 0;

                        var segment = new Segment
                        {
                            ProjectId = project.Id,
                            TrackId = audioTrack.Id,
                            StartTime = 0,
                            EndTime = duration > 0 ? duration : 600, // fallback 10 min
                            Text = asset.Name ?? "Main Audio",
                            BackgroundAssetId = asset.Id,
                            Kind = SegmentKinds.Audio,
                            Volume = 1.0,
                            Order = 0
                        };

                        audioTrack.Segments.Add(segment);
                        _context.Segments.Add(segment);
                        await _context.SaveChangesAsync();

                        Log.Information("Audio segment created from project audio: {AssetId}, duration {Duration}s", asset.Id, duration);
                            project.Tracks = new List<Track>
                            {
                                new Track { ProjectId = project.Id, Order = 0, TrackType = TrackTypes.Text, Name = "Text 1" },
                                new Track { ProjectId = project.Id, Order = 1, TrackType = TrackTypes.Visual, Name = "Visual 1" },
                                new Track { ProjectId = project.Id, Order = 2, TrackType = TrackTypes.Audio, Name = "Audio", IsLocked = false, IsVisible = true }
                            };
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating project");
                throw;
            try
            catch (Exception ex)
        }
            {
                return await _context.Projects
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

            // ── 1. Migrate Project.AudioPath → audio segment ────────────────
            if (!string.IsNullOrWhiteSpace(project.AudioPath) && File.Exists(project.AudioPath))
            {
                // Check if audio track already has segments (already migrated)
                var audioTrack = project.Tracks?.FirstOrDefault(t =>
                    string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));

                bool hasAudioSegments = audioTrack?.Segments?.Any(s =>
                    !string.IsNullOrWhiteSpace(s.BackgroundAssetId)) == true;

                if (!hasAudioSegments)
                {
                    // Need to use a tracked context for writes
                    var trackedProject = await _context.Projects
                        .Include(p => p.Tracks).ThenInclude(t => t.Segments)
                        .Include(p => p.Assets)
                        .FirstOrDefaultAsync(p => p.Id == project.Id);

                    if (trackedProject != null)
                    {
                        var trackedAudioTrack = trackedProject.Tracks?.FirstOrDefault(t =>
                            string.Equals(t.TrackType, TrackTypes.Audio, StringComparison.OrdinalIgnoreCase));

                        if (trackedAudioTrack != null)
                        {
                            try
                            {
                                // Check if this audio file is already an asset in the project
                                var existingAsset = trackedProject.Assets?.FirstOrDefault(a =>
                                    string.Equals(a.Type, "Audio", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(a.FilePath)
                                    && File.Exists(a.FilePath));

                                Asset asset;
                                if (existingAsset != null)
                                {
                                    asset = existingAsset;
                                }
                                else
                                {
                                    asset = await AddAssetAsync(trackedProject.Id, trackedProject.AudioPath, "Audio");
                                }

                                double duration = asset.Duration ?? 0;
                                var segment = new Segment
                                {
                                    ProjectId = trackedProject.Id,
                                    TrackId = trackedAudioTrack.Id,
                                    StartTime = 0,
                                    EndTime = duration > 0 ? duration : 600,
                                    Text = asset.Name ?? "Main Audio",
                                    BackgroundAssetId = asset.Id,
                                    Kind = SegmentKinds.Audio,
                                    Volume = 1.0,
                                    Order = 0
                                };

                                trackedAudioTrack.Segments ??= new List<Segment>();
                                trackedAudioTrack.Segments.Add(segment);
                                _context.Segments.Add(segment);

                                migrated = true;
                                Log.Information("Migrated AudioPath → audio segment for project {ProjectId}", project.Id);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Failed to migrate AudioPath to segment for project {ProjectId}", project.Id);
                            }
                        }
                    }
                }
            }

            // ── 2. Migrate BgmTracks → audio segments ───────────────────────
            if (project.BgmTracks?.Count > 0)
            {
                var trackedProject = await _context.Projects
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
                                Name = "BGM",
                                IsLocked = false,
                                IsVisible = true
                            };
                            trackedProject.Tracks ??= new List<Track>();
                            trackedProject.Tracks.Add(bgmTrack);
                            _context.Tracks.Add(bgmTrack);
                            await _context.SaveChangesAsync(); // need ID for segment FK
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
                                _context.Segments.Add(segment);

                                // Remove old BgmTrack record
                                _context.BgmTracks.Remove(bgm);

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
            }

            if (migrated)
                await _context.SaveChangesAsync();

            return migrated;
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
                var track = await _context.Tracks.FindAsync(trackId);
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
                var oldSegments = _context.Segments.Where(s => s.TrackId == trackId).ToList();
                foreach (var s in oldSegments)
                    _context.Segments.Remove(s);

                // Add new segments
                foreach (var s in list)
                    _context.Segments.Add(s);

                project.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

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
                return await _context.Tracks
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
                return await _context.Tracks
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
                _context.Tracks.Update(track);
                await _context.SaveChangesAsync();

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
            _context.Elements.Add(element);
            await _context.SaveChangesAsync();
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

            _context.Elements.Update(element);
            await _context.SaveChangesAsync();
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
                var track = await _context.Tracks.FindAsync(trackId);
                if (track == null)
                    throw new KeyNotFoundException($"Track {trackId} not found");

                _context.Tracks.Remove(track);
                await _context.SaveChangesAsync();

                Log.Information("Track deleted: {TrackId}", trackId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting track {TrackId}", trackId);
                throw;
            }
        }

        /// <summary>
        /// Replace all elements of a project with a new list (for canvas save).
        /// Old elements are removed and new ones inserted in a single transaction.
        /// </summary>
        public async Task ReplaceElementsAsync(string projectId, IEnumerable<Element> newElements)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));

            try
            {
                var oldElements = _context.Elements.Where(e => e.ProjectId == projectId).ToList();
                foreach (var el in oldElements)
                    _context.Elements.Remove(el);

                var elementList = newElements.ToList();
                foreach (var el in elementList)
                {
                    el.ProjectId = projectId;
                    _context.Elements.Add(el);
                }

                await _context.SaveChangesAsync();
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

                // Determine which BgmTracks already exist in DB BEFORE attaching the
                // object graph. Projects.Update() marks every reachable entity as
                // Modified; new BgmTrack rows (not yet in DB) must be re-marked as
                // Added so EF emits INSERT instead of a silent no-op UPDATE.
                var existingBgmIds = project.BgmTracks?.Count > 0
                    ? new HashSet<string>(
                          await _context.BgmTracks
                              .Where(b => b.ProjectId == project.Id)
                              .Select(b => b.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                // Same for Tracks and Segments — newly added tracks/segments need Added state.
                var existingTrackIds = project.Tracks?.Count > 0
                    ? new HashSet<string>(
                          await _context.Tracks
                              .Where(t => t.ProjectId == project.Id)
                              .Select(t => t.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                var existingSegmentIds = project.Tracks?.Any(t => t.Segments?.Count > 0) == true
                    ? new HashSet<string>(
                          await _context.Segments
                              .Where(s => s.ProjectId == project.Id)
                              .Select(s => s.Id)
                              .ToListAsync())
                    : new HashSet<string>();

                _context.Projects.Update(project);

                // Fix up any brand-new BgmTrack entities that were incorrectly
                // stamped Modified by the graph walk above.
                if (project.BgmTracks != null)
                    foreach (var bgm in project.BgmTracks)
                        if (!existingBgmIds.Contains(bgm.Id))
                            _context.Entry(bgm).State = EntityState.Added;

                // Fix up new Track and Segment entities.
                if (project.Tracks != null)
                {
                    foreach (var track in project.Tracks)
                    {
                        if (!existingTrackIds.Contains(track.Id))
                            _context.Entry(track).State = EntityState.Added;

                        if (track.Segments != null)
                            foreach (var seg in track.Segments)
                                if (!existingSegmentIds.Contains(seg.Id))
                                    _context.Entry(seg).State = EntityState.Added;
                    }
                }

                await _context.SaveChangesAsync();

                Log.Information("Project updated: {ProjectId}", project.Id);
                return project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating project {ProjectId}", project.Id);
                throw;
            }
        }

        /// <summary>
        /// Delete project by ID.
        /// </summary>
        public async Task DeleteProjectAsync(string projectId)
        {
            try
            {
                var project = await _context.Projects.FindAsync(projectId);
                if (project == null)
                    throw new KeyNotFoundException($"Project {projectId} not found");

                _context.Projects.Remove(project);
                await _context.SaveChangesAsync();

                Log.Information("Project deleted: {ProjectId}", projectId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting project {ProjectId}", projectId);
                throw;
            }
        }

        /// <summary>
        /// Get recent projects (last 5).
        /// Includes tracks and their segments - essential for timeline display.
        /// Assets, Elements, and BGM are loaded on-demand when project is opened.
        /// </summary>
        public async Task<List<Project>> GetRecentProjectsAsync(int count = 5)
        {
            try
            {
                return await _context.Projects
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
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
