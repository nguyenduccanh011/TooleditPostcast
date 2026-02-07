#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
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
        /// Create a new project.
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

                _context.Projects.Add(project);
                await _context.SaveChangesAsync();

                Log.Information("Project created: {ProjectId} - {ProjectName}", project.Id, project.Name);
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
        /// Get project by ID.
        /// </summary>
        public async Task<Project?> GetProjectAsync(string projectId)
        {
            try
            {
                return await _context.Projects
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
