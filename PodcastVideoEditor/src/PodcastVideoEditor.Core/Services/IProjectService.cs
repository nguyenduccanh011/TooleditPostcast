#nullable enable
using PodcastVideoEditor.Core.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core.Services
{
    /// <summary>
    /// Contract for project-level CRUD and asset management operations.
    /// Implemented by <see cref="ProjectService"/>; abstract contract enables testability.
    /// </summary>
    public interface IProjectService
    {
        // ── Project ──────────────────────────────────────────────────────────────────
        Task<Project> CreateProjectAsync(string name, string? audioPath = null);
        Task<Project?> GetProjectAsync(string projectId);
        Task<Project> UpdateProjectAsync(Project project);
        Task DeleteProjectAsync(string projectId);
        Task<List<Project>> GetAllProjectsAsync();
        Task<List<Project>> GetRecentProjectsAsync(int count = 5);

        // ── Asset ────────────────────────────────────────────────────────────────────
        Task<Asset> AddAssetAsync(string projectId, string sourceFilePath, string type = "Image");
        Task<Asset?> GetAssetByIdAsync(string assetId);

        // ── Track ────────────────────────────────────────────────────────────────────
        Task<List<Track>> GetTracksAsync(string projectId);
        Task<Track?> GetTrackByIdAsync(string trackId);
        Task<Track> UpdateTrackAsync(Track track);
        Task DeleteTrackAsync(string trackId);
        Task ReplaceSegmentsOfTrackAsync(Project project, string trackId, IEnumerable<Segment> newSegments);

        // ── Element ──────────────────────────────────────────────────────────────────
        Task<Element> AddElementAsync(string projectId, Element element);
        Task<Element> UpdateElementAsync(Element element);
        Task ReplaceElementsAsync(string projectId, IEnumerable<Element> newElements);

        // ── Migration ────────────────────────────────────────────────────────────────
        Task<bool> MigrateToAudioSegmentsAsync(Project project);
    }
}
