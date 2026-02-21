#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Service for database operations (CRUD for projects, segments, elements, etc.)
/// </summary>
public class DatabaseService
{
    private readonly AppDbContext _context;

    public DatabaseService(AppDbContext context)
    {
        _context = context;
    }

    #region Project Operations

    /// <summary>
    /// Get all projects
    /// </summary>
    public async Task<List<Project>> GetAllProjectsAsync()
    {
        try
        {
            return await _context.Projects
                .AsNoTracking()
                .AsSplitQuery()
                .Include(p => p.Segments)
                .Include(p => p.Elements)
                .Include(p => p.Assets)
                .Include(p => p.BgmTracks)
                .OrderByDescending(p => p.UpdatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving projects");
            throw;
        }
    }

    /// <summary>
    /// Get project by ID
    /// </summary>
    public async Task<Project?> GetProjectByIdAsync(string projectId)
    {
        try
        {
            return await _context.Projects
                .AsNoTracking()
                .AsSplitQuery()
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
    /// Create new project
    /// </summary>
    public async Task<Project> CreateProjectAsync(string name, string description = "")
    {
        try
        {
            var project = new Project
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = description,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
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
    /// Update project
    /// </summary>
    public async Task<Project> UpdateProjectAsync(Project project)
    {
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
    /// Delete project (cascade deletes segments, elements, etc.)
    /// </summary>
    public async Task DeleteProjectAsync(string projectId)
    {
        try
        {
            var project = await _context.Projects.FindAsync(projectId);
            if (project != null)
            {
                _context.Projects.Remove(project);
                await _context.SaveChangesAsync();
                Log.Information("Project deleted: {ProjectId}", projectId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting project {ProjectId}", projectId);
            throw;
        }
    }

    #endregion

    #region Segment Operations

    /// <summary>
    /// Get segments for a project
    /// </summary>
    public async Task<List<Segment>> GetSegmentsByProjectAsync(string projectId)
    {
        try
        {
            return await _context.Segments
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.Order)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving segments for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Create segment
    /// </summary>
    public async Task<Segment> CreateSegmentAsync(string projectId, double startTime, double endTime, string text)
    {
        try
        {
            // Get max order for this project
            var maxOrder = await _context.Segments
                .Where(s => s.ProjectId == projectId)
                .MaxAsync(s => (int?)s.Order) ?? 0;

            var segment = new Segment
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = projectId,
                StartTime = startTime,
                EndTime = endTime,
                Text = text,
                Order = maxOrder + 1
            };

            _context.Segments.Add(segment);
            await _context.SaveChangesAsync();
            Log.Information("Segment created: {SegmentId} in project {ProjectId}", segment.Id, projectId);
            return segment;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating segment for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Update segment
    /// </summary>
    public async Task<Segment> UpdateSegmentAsync(Segment segment)
    {
        try
        {
            _context.Segments.Update(segment);
            await _context.SaveChangesAsync();
            Log.Information("Segment updated: {SegmentId}", segment.Id);
            return segment;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating segment {SegmentId}", segment.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete segment
    /// </summary>
    public async Task DeleteSegmentAsync(string segmentId)
    {
        try
        {
            var segment = await _context.Segments.FindAsync(segmentId);
            if (segment != null)
            {
                _context.Segments.Remove(segment);
                await _context.SaveChangesAsync();
                Log.Information("Segment deleted: {SegmentId}", segmentId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting segment {SegmentId}", segmentId);
            throw;
        }
    }

    #endregion

    #region Element Operations

    /// <summary>
    /// Get elements for a project
    /// </summary>
    public async Task<List<Element>> GetElementsByProjectAsync(string projectId)
    {
        try
        {
            return await _context.Elements
                .Where(e => e.ProjectId == projectId)
                .OrderBy(e => e.ZIndex)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving elements for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Create element
    /// </summary>
    public async Task<Element> CreateElementAsync(string projectId, string type, double x, double y)
    {
        try
        {
            var maxZIndex = await _context.Elements
                .Where(e => e.ProjectId == projectId)
                .MaxAsync(e => (int?)e.ZIndex) ?? 0;

            var element = new Element
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = projectId,
                Type = type,
                X = x,
                Y = y,
                ZIndex = maxZIndex + 1
            };

            _context.Elements.Add(element);
            await _context.SaveChangesAsync();
            Log.Information("Element created: {ElementId} in project {ProjectId}", element.Id, projectId);
            return element;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating element for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Update element
    /// </summary>
    public async Task<Element> UpdateElementAsync(Element element)
    {
        try
        {
            _context.Elements.Update(element);
            await _context.SaveChangesAsync();
            Log.Information("Element updated: {ElementId}", element.Id);
            return element;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating element {ElementId}", element.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete element
    /// </summary>
    public async Task DeleteElementAsync(string elementId)
    {
        try
        {
            var element = await _context.Elements.FindAsync(elementId);
            if (element != null)
            {
                _context.Elements.Remove(element);
                await _context.SaveChangesAsync();
                Log.Information("Element deleted: {ElementId}", elementId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting element {ElementId}", elementId);
            throw;
        }
    }

    #endregion

    #region Asset Operations

    /// <summary>
    /// Get assets for a project
    /// </summary>
    public async Task<List<Asset>> GetAssetsByProjectAsync(string projectId)
    {
        try
        {
            return await _context.Assets
                .Where(a => a.ProjectId == projectId)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving assets for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Get asset by ID
    /// </summary>
    public async Task<Asset?> GetAssetByIdAsync(string assetId)
    {
        try
        {
            return await _context.Assets.FindAsync(assetId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving asset {AssetId}", assetId);
            throw;
        }
    }

    /// <summary>
    /// Create asset
    /// </summary>
    public async Task<Asset> CreateAssetAsync(string projectId, string name, string type, string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var asset = new Asset
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = projectId,
                Name = name,
                Type = type,
                FilePath = filePath,
                FileName = fileInfo.Name,
                Extension = fileInfo.Extension,
                FileSize = fileInfo.Length,
                CreatedAt = DateTime.UtcNow
            };

            _context.Assets.Add(asset);
            await _context.SaveChangesAsync();
            Log.Information("Asset created: {AssetId} in project {ProjectId}", asset.Id, projectId);
            return asset;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating asset for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Delete asset
    /// </summary>
    public async Task DeleteAssetAsync(string assetId)
    {
        try
        {
            var asset = await _context.Assets.FindAsync(assetId);
            if (asset != null)
            {
                _context.Assets.Remove(asset);
                await _context.SaveChangesAsync();
                Log.Information("Asset deleted: {AssetId}", assetId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting asset {AssetId}", assetId);
            throw;
        }
    }

    #endregion

    #region Template Operations

    /// <summary>
    /// Get all templates
    /// </summary>
    public async Task<List<Template>> GetAllTemplatesAsync()
    {
        try
        {
            return await _context.Templates
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving templates");
            throw;
        }
    }

    /// <summary>
    /// Create template
    /// </summary>
    public async Task<Template> CreateTemplateAsync(string name, string description, string layoutJson)
    {
        try
        {
            var template = new Template
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = description,
                LayoutJson = layoutJson,
                CreatedAt = DateTime.UtcNow
            };

            _context.Templates.Add(template);
            await _context.SaveChangesAsync();
            Log.Information("Template created: {TemplateId} - {TemplateName}", template.Id, template.Name);
            return template;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating template");
            throw;
        }
    }

    #endregion
}
