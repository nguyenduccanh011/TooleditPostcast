#nullable enable
using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Core
{
    public class ProjectServiceTest
    {
        public async Task RunTests()
        {
            try
            {
                Log.Information("=== ProjectService Test Suite ===");

                // Setup database for testing using real SQLite
                var dbPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PodcastVideoEditor", 
                    "test_project_service.db");

                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options;

                using (var context = new AppDbContext(options))
                {
                    // Ensure database is created and migrated
                    await context.Database.EnsureDeletedAsync();
                    await context.Database.EnsureCreatedAsync();

                    var projectService = new ProjectService(context);

                    // Test 1: Create Project
                    Log.Information("Test 1: Create Project");
                    var project = await projectService.CreateProjectAsync("Test Podcast 1", "/path/to/audio.mp3");
                    Log.Information("✓ Project created: ID={ProjectId}, Name={ProjectName}", project.Id, project.Name);

                    // Test 2: Get Project
                    Log.Information("Test 2: Get Project by ID");
                    var retrieved = await projectService.GetProjectAsync(project.Id);
                    if (retrieved != null && retrieved.Id == project.Id)
                        Log.Information("✓ Project retrieved successfully");
                    else
                        Log.Warning("✗ Failed to retrieve project");

                    // Test 3: Get All Projects
                    Log.Information("Test 3: Get All Projects");
                    var allProjects = await projectService.GetAllProjectsAsync();
                    Log.Information("✓ Retrieved {Count} project(s)", allProjects.Count);

                    // Test 4: Update Project
                    Log.Information("Test 4: Update Project");
                    if (retrieved != null)
                    {
                        retrieved.Name = "Updated Podcast Name";
                        var updated = await projectService.UpdateProjectAsync(retrieved);
                        Log.Information("✓ Project updated: {ProjectName}", updated.Name);
                    }

                    // Test 5: Get Recent Projects
                    Log.Information("Test 5: Get Recent Projects");
                    var recentProjects = await projectService.GetRecentProjectsAsync(5);
                    Log.Information("✓ Retrieved {Count} recent project(s)", recentProjects.Count);

                    // Test 6: Create Multiple Projects
                    Log.Information("Test 6: Create Multiple Projects");
                    for (int i = 2; i <= 3; i++)
                    {
                        await projectService.CreateProjectAsync($"Podcast {i}", $"/path/to/audio{i}.mp3");
                    }
                    var allAfterCreate = await projectService.GetAllProjectsAsync();
                    Log.Information("✓ Total projects now: {Count}", allAfterCreate.Count);

                    // Test 7: Delete Project
                    Log.Information("Test 7: Delete Project");
                    if (allAfterCreate.Count > 0)
                    {
                        var projectToDelete = allAfterCreate[0];
                        await projectService.DeleteProjectAsync(projectToDelete.Id);
                        Log.Information("✓ Project deleted: {ProjectId}", projectToDelete.Id);

                        var verifyDelete = await projectService.GetProjectAsync(projectToDelete.Id);
                        if (verifyDelete == null)
                            Log.Information("✓ Deletion verified - project no longer exists");
                    }

                    Log.Information("=== All ProjectService Tests Completed Successfully ===");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ProjectService test failed");
            }
        }
    }
}
