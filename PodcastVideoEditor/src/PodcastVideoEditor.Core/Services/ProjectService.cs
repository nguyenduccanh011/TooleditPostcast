#nullable enable
using Microsoft.EntityFrameworkCore;
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
                Duration = null,
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
                        TrackType = "text", 
                        Name = "Text 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    },
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 1, 
                        TrackType = "visual", 
                        Name = "Visual 1", 
                        IsLocked = false, 
                        IsVisible = true 
                    },
                    new Track 
                    { 
                        ProjectId = project.Id, 
                        Order = 2, 
                        TrackType = "audio", 
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
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
                    .Include(p => p.Segments)
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
                    .Include(p => p.Tracks)
                    .ThenInclude(t => t.Segments)
                    .Include(p => p.Segments)
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
        /// Replace all segments of a project with a new list (kept for backward compatibility).
        /// </summary>
        [Obsolete("Use ReplaceSegmentsOfTrackAsync instead for multi-track support")]
        public async Task ReplaceSegmentsAsync(Project project, IEnumerable<Segment> newSegments)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            var list = newSegments.ToList();
            var projectId = project.Id;
            foreach (var s in list)
                s.ProjectId = projectId;

            // Copy current segments from navigation (tracked), then clear and remove from context
            // to avoid EF Core tracking conflict when mixing RemoveRange + modified collection.
            var toRemove = project.Segments.ToList();
            project.Segments.Clear();
            foreach (var s in toRemove)
                _context.Segments.Remove(s);

            foreach (var s in list)
                project.Segments.Add(s);
            project.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            Log.Information("Replaced segments for project {ProjectId}: {Count} segments", projectId, list.Count);
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
        /// Update project.
        /// </summary>
        public async Task<Project> UpdateProjectAsync(Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            try
            {
                project.UpdatedAt = DateTime.UtcNow;
                _context.Projects.Update(project);
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
        /// </summary>
        public async Task<List<Project>> GetRecentProjectsAsync(int count = 5)
        {
            try
            {
                return await _context.Projects
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
