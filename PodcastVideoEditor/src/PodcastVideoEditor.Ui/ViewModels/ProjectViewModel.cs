#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Ui.Converters;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for project management (MVVM Toolkit).
    /// </summary>
    public partial class ProjectViewModel : ObservableObject, IDisposable
    {
        private readonly IProjectService _projectService;
        private readonly CanvasViewModel _canvasViewModel;
        private readonly RenderViewModel _renderViewModel;
        private readonly ThumbnailPreGenerationService _thumbnailService;
        private readonly ImageAssetIngestService _imageAssetIngestService;
        private readonly PropertyChangedEventHandler _canvasPropertyChangedHandler;
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

        /// <summary>
        /// Expose project service for ViewModels that need direct DB operations
        /// (e.g. track deletion that must persist immediately).
        /// </summary>
        public IProjectService ProjectService => _projectService;

        [ObservableProperty]
        private Project? currentProject;

        [ObservableProperty]
        private ObservableCollection<Project> projects = new();

        [ObservableProperty]
        private string newProjectName = string.Empty;

        [ObservableProperty]
        private string selectedAudioPath = string.Empty;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = string.Empty;

        /// <summary>Audio assets for the current project (Audio type only). Kept in sync by the ViewModel — bind directly, no code-behind filter needed.</summary>
        public ObservableCollection<Asset> AudioAssets { get; } = new();

        private bool _disposed;

        public ProjectViewModel(
            IProjectService projectService,
            CanvasViewModel canvasViewModel,
            RenderViewModel renderViewModel,
            ImageAssetIngestService imageAssetIngestService)
        {
            _projectService = projectService;
            _canvasViewModel = canvasViewModel;
            _renderViewModel = renderViewModel;
            _thumbnailService = new ThumbnailPreGenerationService(maxConcurrent: 2);
            _imageAssetIngestService = imageAssetIngestService;

            // Sync preview aspect ratio to render whenever user changes it in canvas
            _canvasPropertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(CanvasViewModel.SelectedAspectRatio))
                    _renderViewModel.SelectedAspectRatio = _canvasViewModel.SelectedAspectRatio;
            };
            _canvasViewModel.PropertyChanged += _canvasPropertyChangedHandler;
            _renderViewModel.SelectedAspectRatio = _canvasViewModel.SelectedAspectRatio;
            _renderViewModel.ApplyProjectRenderSettings(new RenderSettings
            {
                AspectRatio = _canvasViewModel.SelectedAspectRatio
            });
        }

        /// <summary>
        /// Called when CurrentProject changes (MVVM Toolkit auto-generated partial method).
        /// </summary>
        partial void OnCurrentProjectChanged(Project? value)
        {
            SegmentThumbnailSourceConverter.ClearCache();
            SegmentThumbnailBrushConverter.ClearCache();
            SegmentStripTimesConverter.ClearCache();
            VideoFrameAtTimeConverter.ClearCache();

            // Log only when project is selected (not NULL)
            if (value != null)
                Log.Information("Project selected: {ProjectName}", value.Name);

            RefreshAudioAssets();
        }

        private void RefreshAudioAssets()
        {
            AudioAssets.Clear();
            if (CurrentProject == null) return;
            foreach (var a in CurrentProject.Assets.Where(a => string.Equals(a.Type, "Audio", StringComparison.OrdinalIgnoreCase)))
                AudioAssets.Add(a);
        }

        /// <summary>
        /// Load all projects from database.
        /// </summary>
        [RelayCommand]
        public async Task LoadProjectsAsync()
        {
            if (IsLoading)
                return; // Prevent re-entrant loads

            IsLoading = true;
            StatusMessage = "Loading projects...";


            // Capture the LIVE reference before Projects.Clear() triggers the
            // ListBox two-way binding which would set CurrentProject = null.
            var savedProject = CurrentProject;
            var savedProjectId = savedProject?.Id;
            try
            {
                // Use recent-first list to mirror commercial UX of "Recent Projects" and keep the list lean.
                var projectList = await _projectService.GetRecentProjectsAsync(10);
                
                Projects.Clear();

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var project in projectList)
                {
                    if (seen.Add(project.Id))
                        Projects.Add(project);
                }

                // Restore the fully-loaded in-memory project reference.
                // IMPORTANT: Do NOT replace with the shallow DB copy from
                // GetRecentProjectsAsync (which lacks Assets/Elements/BgmTracks).
                // The saved instance has all in-memory edits (track deletions,
                // segment changes, WaveformPeaks, etc.) — replacing it would
                // reload stale data from the DB and undo unsaved changes.
                if (savedProject != null && !string.IsNullOrEmpty(savedProjectId))
                {
                    // Only restore if the project still exists in the DB list
                    var stillExists = Projects.Any(p => p.Id == savedProjectId);
                    if (stillExists)
                        CurrentProject = savedProject;
                }

                StatusMessage = $"Loaded {projectList.Count} project(s)";
                Log.Information("Loaded {Count} project(s)", projectList.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading projects: {ex.Message}";
                Log.Error(ex, "Error loading projects");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Create a new project.
        /// </summary>
        [RelayCommand]
        public async Task CreateProjectAsync()
        {
            if (string.IsNullOrWhiteSpace(NewProjectName))
            {
                StatusMessage = "Project name is required";
                return;
            }

            IsLoading = true;
            StatusMessage = "Creating project...";

            try
            {
                var project = await _projectService.CreateProjectAsync(NewProjectName, SelectedAudioPath);
                Projects.Add(project);
                CurrentProject = project;

                // Initialize render/preview settings in UI from the new project's settings
                var aspect = project.RenderSettings.AspectRatio;
                if (!string.IsNullOrWhiteSpace(aspect))
                {
                    _canvasViewModel.SelectedAspectRatio = aspect;
                }
                _renderViewModel.ApplyProjectRenderSettings(project.RenderSettings);

                // Clear canvas elements — new project has none
                _canvasViewModel.LoadElementsFromProject();

                // Reset form
                NewProjectName = string.Empty;
                SelectedAudioPath = string.Empty;

                StatusMessage = $"Project '{project.Name}' created successfully";
                Log.Information("Project created: {ProjectName}", project.Name);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creating project: {ex.Message}";
                Log.Error(ex, "Error creating project");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Create a new project from a template and keep UI state in sync.
        /// </summary>
        public async Task<Project?> CreateProjectFromTemplateAsync(string projectName, string templateId, string? audioPath)
        {
            if (string.IsNullOrWhiteSpace(projectName))
            {
                StatusMessage = "Project name is required";
                return null;
            }

            IsLoading = true;
            StatusMessage = "Creating project from template...";

            try
            {
                var project = await _projectService.CreateProjectFromTemplateAsync(projectName, templateId, audioPath);
                if (!Projects.Any(p => p.Id == project.Id))
                    Projects.Add(project);

                CurrentProject = project;

                var aspect = project.RenderSettings.AspectRatio;
                if (!string.IsNullOrWhiteSpace(aspect))
                    _canvasViewModel.SelectedAspectRatio = aspect;

                _renderViewModel.ApplyProjectRenderSettings(project.RenderSettings);
                _canvasViewModel.LoadElementsFromProject();

                StatusMessage = $"Project '{project.Name}' created successfully";
                Log.Information("Project created from template: {ProjectName} ({TemplateId})", project.Name, templateId);
                return project;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creating project from template: {ex.Message}";
                Log.Error(ex, "Error creating project from template {TemplateId}", templateId);
                return null;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Open/select a project.
        /// </summary>
        [RelayCommand]
        public async Task OpenProjectAsync(Project project)
        {
            if (project == null)
            {
                StatusMessage = "No project selected";
                return;
            }

            IsLoading = true;
            StatusMessage = $"Opening project '{project.Name}'...";

            try
            {
                var loadedProject = await _projectService.GetProjectAsync(project.Id);
                if (loadedProject != null)
                {
                    // Auto-migrate legacy AudioPath/BgmTrack → audio segments
                    bool needsReload = await _projectService.MigrateToAudioSegmentsAsync(loadedProject);
                    if (needsReload)
                    {
                        loadedProject = await _projectService.GetProjectAsync(project.Id);
                        if (loadedProject == null) { StatusMessage = "Project not found after migration"; return; }
                    }

                    _thumbnailService.Stop();
                    CurrentProject = loadedProject;

                    // Sync render/preview settings from project into UI view models
                    var aspect = loadedProject.RenderSettings.AspectRatio;
                    if (!string.IsNullOrWhiteSpace(aspect))
                    {
                        _canvasViewModel.SelectedAspectRatio = aspect;
                    }
                    _renderViewModel.ApplyProjectRenderSettings(loadedProject.RenderSettings);

                    // Load canvas elements from project DB data
                    _canvasViewModel.LoadElementsFromProject();

                    StatusMessage = $"Project opened: {loadedProject.Name}";
                    Log.Information("Project opened: {ProjectId} - {ProjectName}", loadedProject.Id, loadedProject.Name);
                    
                    // Pre-generate thumbnails in background for smooth timeline scrubbing
                    _ = _thumbnailService.PreGenerateThumbnailsForProjectAsync(loadedProject);
                }
                else
                {
                    StatusMessage = "Project not found";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error opening project: {ex.Message}";
                Log.Error(ex, "Error opening project");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Replace segments of the text track in current project and save (ST-3 script apply).
        /// Finds the first text track (typically "Text 1") and replaces its segments.
        /// </summary>
        public async Task ReplaceSegmentsAndSaveAsync(IEnumerable<Segment> newSegments)
        {
            if (CurrentProject == null)
                throw new InvalidOperationException("No project loaded");

            // Find preferred script text track first, then fall back to first text track.
            var textTrack = CurrentProject.Tracks?
                .FirstOrDefault(t => string.Equals(t.TrackRole, TrackRoles.ScriptText, StringComparison.OrdinalIgnoreCase))
                ?? CurrentProject.Tracks?.FirstOrDefault(t => t.TrackType == "text");
            if (textTrack == null)
                throw new InvalidOperationException("No text track found in project");

            // Use new ReplaceSegmentsOfTrackAsync for multi-track support
            await _projectService.ReplaceSegmentsOfTrackAsync(CurrentProject, textTrack.Id, newSegments);

            // Reload project from DB so in-memory Tracks/Segments match (fixes timeline not updating after Apply Script)
            var refreshed = await _projectService.GetProjectAsync(CurrentProject.Id);
            if (refreshed != null)
                CurrentProject = refreshed;
        }

        /// <summary>
        /// Replace all segments on any track (by track ID) and reload the project from DB.
        /// Used by AnalyzeWithAICommand to persist both text and visual tracks.
        /// </summary>
        public async Task ReplaceSegmentsForTrackAsync(string trackId, IEnumerable<Segment> newSegments)
        {
            if (CurrentProject == null)
                throw new InvalidOperationException("No project loaded");

            await _projectService.ReplaceSegmentsOfTrackAsync(CurrentProject, trackId, newSegments);

            var refreshed = await _projectService.GetProjectAsync(CurrentProject.Id);
            if (refreshed != null)
                CurrentProject = refreshed;
        }

        public async Task<int> StretchDynamicVisualOverlaysAsync()
        {
            if (CurrentProject == null)
                return 0;

            var changed = await _projectService.StretchDynamicVisualOverlaysAsync(CurrentProject.Id);
            if (changed > 0)
            {
                var refreshed = await _projectService.GetProjectAsync(CurrentProject.Id);
                if (refreshed != null)
                    CurrentProject = refreshed;
            }

            return changed;
        }

        /// <summary>
        /// Download an image from <paramref name="url"/>, register it as an asset,
        /// and return its asset ID. Returns null on failure.
        /// Used by the segment image picker to apply a user-selected candidate.
        /// </summary>
        public async Task<string?> AddImageFromUrlAsync(string url, string candidateId)
        {
            if (CurrentProject == null) return null;
            PreparedImageAsset? prepared = null;
            try
            {
                prepared = await _imageAssetIngestService.DownloadAndPrepareAsync(url, candidateId);
                var asset = await _projectService.AddAssetAsync(
                    CurrentProject.Id,
                    prepared.FilePath,
                    "Image");
                return asset.Id;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "AddImageFromUrlAsync failed for {CandidateId}", candidateId);
                return null;
            }
            finally
            {
                if (prepared != null)
                {
                    try { System.IO.File.Delete(prepared.FilePath); } catch { /* best-effort */ }
                }
            }
        }

        /// <summary>
        /// Save current project.
        /// </summary>
        [RelayCommand]
        public async Task SaveProjectAsync()
        {
            if (CurrentProject == null)
            {
                StatusMessage = "No project to save";
                return;
            }

            // Prevent concurrent saves (fire-and-forget callers, Ctrl+S spam, autosave overlap)
            if (!await _saveSemaphore.WaitAsync(0))
                return;

            StatusMessage = "Saving project...";

            try
            {
                // Sync aspect ratio from UI (canvas) back into project settings before saving
                if (!string.IsNullOrWhiteSpace(_canvasViewModel.SelectedAspectRatio))
                {
                    _renderViewModel.SelectedAspectRatio = _canvasViewModel.SelectedAspectRatio;
                }
                CurrentProject.RenderSettings = _renderViewModel.BuildProjectRenderSettings();

                // Save project (tracks, segments) BEFORE elements so that FK
                // references from Element.SegmentId to Segment.Id are satisfied.
                await _projectService.UpdateProjectAsync(CurrentProject);

                // Save canvas elements to DB (after segments exist in DB)
                await _canvasViewModel.SaveElementsAsync(_projectService);
                StatusMessage = $"Project '{CurrentProject.Name}' saved successfully";
                Log.Information("Project saved: {ProjectId}", CurrentProject.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving project: {ex.Message}";
                Log.Error(ex, "Error saving project");
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        /// <summary>
        /// Delete a project.
        /// </summary>
        [RelayCommand]
        public async Task DeleteProjectAsync(Project project)
        {
            if (project == null)
            {
                StatusMessage = "No project selected";
                return;
            }

            IsLoading = true;
            StatusMessage = $"Deleting project '{project.Name}'...";

            try
            {
                await _projectService.DeleteProjectAsync(project.Id);
                Projects.Remove(project);

                if (CurrentProject?.Id == project.Id)
                {
                    CurrentProject = null;
                }

                StatusMessage = $"Project '{project.Name}' deleted";
                Log.Information("Project deleted: {ProjectId}", project.Id);
                _thumbnailService.Stop();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting project: {ex.Message}";
                Log.Error(ex, "Error deleting project");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Set selected audio path (called from dialog).
        /// </summary>
        public void SetAudioPath(string audioPath)
        {
            SelectedAudioPath = audioPath;
        }

        /// <summary>
        /// Add an asset to the current project and persist it. Returns the created asset or null when no project is loaded.
        /// </summary>
        public async Task<Asset?> AddAssetToCurrentProjectAsync(string filePath, string type = "Image")
        {
            if (CurrentProject == null)
            {
                StatusMessage = "No project loaded";
                return null;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                StatusMessage = "Invalid file path";
                return null;
            }

            try
            {
                var asset = await _projectService.AddAssetAsync(CurrentProject.Id, filePath, type);
                // EF Core navigation fix-up (singleton DbContext) may have already added the
                // asset to CurrentProject.Assets when _context.Assets.Add() was called inside
                // AddAssetAsync. Guard against the double-add that would cause duplicates in UI.
                if (!CurrentProject.Assets.Any(a => a.Id == asset.Id))
                    CurrentProject.Assets.Add(asset);
                if (string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase)
                    && !AudioAssets.Any(a => a.Id == asset.Id))
                    AudioAssets.Add(asset);
                StatusMessage = $"Asset added: {asset.FileName}";
                Log.Information("Asset added to current project {ProjectId}: {AssetId}", CurrentProject.Id, asset.Id);
                return asset;
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException?.Message;
                StatusMessage = innerMsg != null
                    ? $"Error adding asset: {innerMsg}"
                    : $"Error adding asset: {ex.Message}";
                Log.Error(ex, "Error adding asset to project {ProjectId}", CurrentProject.Id);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _canvasViewModel.PropertyChanged -= _canvasPropertyChangedHandler;
            _thumbnailService.Stop();
            _saveSemaphore.Dispose();
        }

        /// <summary>
        /// Deletes asset files (disk + DB) that are no longer referenced by any segment.
        /// Safe to call after AI re-analysis. Returns purge count.
        /// </summary>
        public Task<int> PurgeUnusedProjectAssetsAsync(string projectId)
            => _projectService.PurgeUnusedAssetsAsync(projectId);
    }
}
