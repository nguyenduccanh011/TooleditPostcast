#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    /// <summary>
    /// ViewModel for project management (MVVM Toolkit).
    /// </summary>
    public partial class ProjectViewModel : ObservableObject
    {
        private readonly ProjectService _projectService;
        private readonly CanvasViewModel _canvasViewModel;
        private readonly RenderViewModel _renderViewModel;

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

        public ProjectViewModel(ProjectService projectService, CanvasViewModel canvasViewModel, RenderViewModel renderViewModel)
        {
            _projectService = projectService;
            _canvasViewModel = canvasViewModel;
            _renderViewModel = renderViewModel;
        }

        /// <summary>
        /// Load all projects from database.
        /// </summary>
        [RelayCommand]
        public async Task LoadProjectsAsync()
        {
            IsLoading = true;
            StatusMessage = "Loading projects...";

            try
            {
                var projectList = await _projectService.GetAllProjectsAsync();
                Projects.Clear();

                foreach (var project in projectList)
                {
                    Projects.Add(project);
                }

                StatusMessage = $"Loaded {projectList.Count} project(s)";
                Log.Information("Projects loaded: {Count}", projectList.Count);
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

            if (string.IsNullOrWhiteSpace(SelectedAudioPath))
            {
                StatusMessage = "Audio file is required";
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
                    _renderViewModel.SelectedAspectRatio = aspect;
                }

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
                    CurrentProject = loadedProject;

                    // Sync render/preview settings from project into UI view models
                    var aspect = loadedProject.RenderSettings.AspectRatio;
                    if (!string.IsNullOrWhiteSpace(aspect))
                    {
                        _canvasViewModel.SelectedAspectRatio = aspect;
                        _renderViewModel.SelectedAspectRatio = aspect;
                    }

                    StatusMessage = $"Project opened: {loadedProject.Name}";
                    Log.Information("Project opened: {ProjectId} - {ProjectName}", loadedProject.Id, loadedProject.Name);
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

            // Find the text track (first track with TrackType = "text")
            var textTrack = CurrentProject.Tracks?.FirstOrDefault(t => t.TrackType == "text");
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

            IsLoading = true;
            StatusMessage = "Saving project...";

            try
            {
                // Sync aspect ratio from UI (canvas) back into project settings before saving
                if (!string.IsNullOrWhiteSpace(_canvasViewModel.SelectedAspectRatio))
                {
                    CurrentProject.RenderSettings.AspectRatio = _canvasViewModel.SelectedAspectRatio;
                }

                await _projectService.UpdateProjectAsync(CurrentProject);
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
                IsLoading = false;
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
                CurrentProject.Assets.Add(asset);
                StatusMessage = $"Asset added: {asset.FileName}";
                Log.Information("Asset added to current project {ProjectId}: {AssetId}", CurrentProject.Id, asset.Id);
                return asset;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding asset: {ex.Message}";
                Log.Error(ex, "Error adding asset to project {ProjectId}", CurrentProject.Id);
                return null;
            }
        }
    }
}
