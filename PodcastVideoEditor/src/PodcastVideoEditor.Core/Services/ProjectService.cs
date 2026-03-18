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
        public async Task<Asset> AddAssetAsync(
            string projectId,
            string sourceFilePath,
            string type = "Image",
            int? width = null,
            int? height = null,
            long? fileSize = null)
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
            var storedFileInfo = new FileInfo(destinationPath);

            if (string.Equals(type, "Image", StringComparison.OrdinalIgnoreCase) && (!width.HasValue || !height.HasValue))
            {
                TryProbeImageDimensions(destinationPath, out width, out height);
            }

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
                FileSize = fileSize ?? storedFileInfo.Length,
                Width = width,
                Height = height,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            };

            _context.Assets.Add(asset);
            await _context.SaveChangesAsync();

            Log.Information("Asset added to project {ProjectId}: {AssetId} ({Name})", projectId, asset.Id, asset.Name);
            return asset;
        }

        private static void TryProbeImageDimensions(string filePath, out int? width, out int? height)
        {
            width = null;
            height = null;

            try
            {
                using var codec = SkiaSharp.SKCodec.Create(filePath);
                if (codec == null)
                    return;

                width = codec.Info.Width;
                height = codec.Info.Height;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not probe image dimensions for {Path}", filePath);
            }
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
        /// </summary>
        public async Task<Project> CreateProjectAsync(string name, string audioPath)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project name cannot be empty", nameof(name));

            if (string.IsNullOrWhiteSpace(audioPath))
                throw new ArgumentException("Audio path cannot be empty", nameof(audioPath));

            try
            {
                var project = new Project
                {
                    Name = name,
                    AudioPath = audioPath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    RenderSettings = new RenderSettings() // Default render settings
                };

                // Create default tracks for the project
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
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 2, 
                        TrackType = TrackTypes.Audio, 
                        Name = "Audio", 
                        IsLocked = false, 
                        IsVisible = true 
                    }
                };

                _context.Projects.Add(project);
                await _context.SaveChangesAsync();

                Log.Information("Project created: {ProjectId} - {ProjectName} with default tracks", project.Id, project.Name);
                return project;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating project");
                throw;
            }
        }

        /// <summary>
        /// Get all projects.
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
                    .Include(p => p.Elements)
                    .Include(p => p.Assets)
                    .Include(p => p.BgmTracks)
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
        /// Get project by ID, including all related data (tracks, segments, etc.).
        /// </summary>
        public async Task<Project?> GetProjectAsync(string projectId)
        {
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

                _context.Projects.Update(project);

                // Fix up any brand-new BgmTrack entities that were incorrectly
                // stamped Modified by the graph walk above.
                if (project.BgmTracks != null)
                    foreach (var bgm in project.BgmTracks)
                        if (!existingBgmIds.Contains(bgm.Id))
                            _context.Entry(bgm).State = EntityState.Added;

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
