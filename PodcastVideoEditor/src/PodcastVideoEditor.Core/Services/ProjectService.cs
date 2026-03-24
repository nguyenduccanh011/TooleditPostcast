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
    public class ProjectService : IProjectService
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
                    }
                };

                _context.Projects.Add(project);
                await _context.SaveChangesAsync();

                // If an audio file was provided, create a dedicated Audio track,
                // import it as an Asset and place a full-duration audio segment on it.
                if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                {
                    try
                    {
                        var audioTrack = new Track
                        {
                            ProjectId = project.Id,
                            Order = 2,
                            TrackType = TrackTypes.Audio,
                            Name = "Audio 1",
                            IsLocked = false,
                            IsVisible = true
                        };
                        project.Tracks.Add(audioTrack);
                        _context.Tracks.Add(audioTrack);
                        await _context.SaveChangesAsync();

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
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not create audio segment from {AudioPath}", audioPath);
                    }
                }

                return project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating project");
                throw;
            }
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

                        // Create audio track if the project doesn't have one (e.g. created after
                        // the default empty audio track was removed, or track was manually deleted).
                        if (trackedAudioTrack == null)
                        {
                            var maxOrder = trackedProject.Tracks?.Max(t => t.Order) ?? 0;
                            trackedAudioTrack = new Track
                            {
                                ProjectId = trackedProject.Id,
                                Order = maxOrder + 1,
                                TrackType = TrackTypes.Audio,
                                Name = "Audio 1",
                                IsLocked = false,
                                IsVisible = true
                            };
                            trackedProject.Tracks ??= new List<Track>();
                            trackedProject.Tracks.Add(trackedAudioTrack);
                            _context.Tracks.Add(trackedAudioTrack);
                            await _context.SaveChangesAsync();
                            Log.Information("Created missing audio track for migration in project {ProjectId}", trackedProject.Id);
                        }

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
            {
                await _context.SaveChangesAsync();

                // Detach all tracked entities left by the migration queries so they
                // don't conflict with subsequent load/update operations (Singleton DbContext).
                _context.ChangeTracker.Clear();
            }

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
        /// Uses ExecuteDeleteAsync (direct SQL) for deletion to avoid EF change-tracker
        /// conflicts with stale tracked instances from prior saves or failed operations.
        /// </summary>
        public async Task ReplaceElementsAsync(string projectId, IEnumerable<Element> newElements)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));

            try
            {
                // Detach any stale tracked elements for this project that may have been
                // left behind by a previous failed UpdateProjectAsync. Without this,
                // Remove(tracked)+Add(new-same-Id) causes EF to revert the tracked entity
                // back to Added state, which then fails SaveChanges with UNIQUE constraint.
                var staleEntries = _context.ChangeTracker.Entries<Element>()
                    .Where(e => e.Entity.ProjectId == projectId)
                    .ToList();
                foreach (var entry in staleEntries)
                    entry.State = EntityState.Detached;

                // Delete existing elements via direct SQL — no tracking conflict possible.
                await _context.Elements
                    .Where(e => e.ProjectId == projectId)
                    .ExecuteDeleteAsync();

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

                // Clear the change tracker to prevent "already tracked" conflicts.
                // With a Singleton DbContext, previous operations (e.g. migration,
                // prior saves) may leave tracked entities that clash with the
                // incoming object graph.  All state decisions below are based on
                // explicit DB queries, so a clean tracker is safe.
                _context.ChangeTracker.Clear();

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

                // Elements are fully managed by ReplaceElementsAsync / SaveElementsAsync.
                // Do NOT query, orphan-delete, or FixNew for elements here — doing so
                // causes tracking conflicts (InvalidOperationException) and UNIQUE constraint
                // failures because project.Elements holds stale AsNoTracking instances that
                // clash with the freshly-tracked instances saved by ReplaceElementsAsync.

                // --- Delete orphaned entities (removed from in-memory but still in DB) ---
                DeleteOrphanedEntities(
                    existingBgmIds,
                    project.BgmTracks?.Select(b => b.Id),
                    _context.BgmTracks);

                DeleteOrphanedEntities(
                    existingTrackIds,
                    project.Tracks?.Select(t => t.Id),
                    _context.Tracks);

                DeleteOrphanedEntities(
                    existingSegmentIds,
                    project.Tracks?.SelectMany(t => t.Segments ?? Enumerable.Empty<Segment>()).Select(s => s.Id),
                    _context.Segments);

                // Exclude project.Elements from the Update() graph walk.
                // Elements are owned exclusively by ReplaceElementsAsync; including them
                // in the graph walk causes tracking conflicts with the AsNoTracking objects
                // stored in project.Elements when elements already exist in the context.
                ICollection<Element> elementsSnapshot = project.Elements;
                project.Elements = [];

                _context.Projects.Update(project);

                // Restore the in-memory collection so callers still see the full project.
                project.Elements = elementsSnapshot;

                // Fix up any brand-new BgmTrack entities that were incorrectly
                // stamped Modified by the graph walk above.
                FixNewEntities(project.BgmTracks, existingBgmIds, b => b.Id);

                // Fix up new Track and Segment entities.
                FixNewEntities(project.Tracks, existingTrackIds, t => t.Id);
                if (project.Tracks != null)
                    foreach (var track in project.Tracks)
                        FixNewEntities(track.Segments, existingSegmentIds, s => s.Id);

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
        /// Delete entities from <paramref name="dbSet"/> whose IDs exist in <paramref name="existingDbIds"/>
        /// but are no longer present in the in-memory <paramref name="currentIds"/> collection.
        /// </summary>
        private static void DeleteOrphanedEntities<T>(
            HashSet<string> existingDbIds,
            IEnumerable<string>? currentIds,
            DbSet<T> dbSet) where T : class
        {
            var keepIds = currentIds != null
                ? new HashSet<string>(currentIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            foreach (var id in existingDbIds)
            {
                if (!keepIds.Contains(id))
                {
                    var entity = dbSet.Local.FirstOrDefault(e => GetEntityId(e) == id)
                                 ?? dbSet.Find(id);
                    if (entity != null)
                        dbSet.Remove(entity);
                }
            }
        }

        /// <summary>
        /// Mark entities as <see cref="EntityState.Added"/> if their ID is not in <paramref name="existingDbIds"/>.
        /// </summary>
        private void FixNewEntities<T>(IEnumerable<T>? entities, HashSet<string> existingDbIds, Func<T, string> idSelector) where T : class
        {
            if (entities == null) return;
            foreach (var entity in entities)
                if (!existingDbIds.Contains(idSelector(entity)))
                    _context.Entry(entity).State = EntityState.Added;
        }

        /// <summary>
        /// Get the Id property value from an entity using convention (all domain entities have string Id).
        /// </summary>
        private static string GetEntityId<T>(T entity) where T : class
        {
            var prop = typeof(T).GetProperty("Id");
            return prop?.GetValue(entity) as string ?? string.Empty;
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
        public async Task<List<Project>> GetAllProjectsAsync()
        {
            try
            {
                return await _context.Projects
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
                return await _context.Projects
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
