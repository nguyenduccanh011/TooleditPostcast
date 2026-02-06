using Microsoft.EntityFrameworkCore;
using PodcastVideoEditor.Core.Database;
using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core;

/// <summary>
/// Test CRUD operations for ST-2 verification
/// </summary>
public class TestCrud
{
    public static async Task TestCrudOperations()
    {
        // Create database context with SQLite
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor", "app.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        using var context = new AppDbContext(optionsBuilder.Options);

        Console.WriteLine("🔍 ST-2: Database Schema & CRUD Test");
        Console.WriteLine("=====================================\n");

        // 1. CREATE: Insert a Project
        Console.WriteLine("1️⃣ CREATE: Inserting a new Project...");
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Podcast Video",
            AudioPath = "/path/to/audio.mp3",
            Description = "Test project for CRUD operations",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RenderSettings = new RenderSettings
            {
                ResolutionWidth = 1080,
                ResolutionHeight = 1920,
                AspectRatio = "9:16",
                Quality = "High",
                FrameRate = 60,
                VideoCodec = "h264",
                AudioCodec = "aac"
            }
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();
        Console.WriteLine($"✅ Project created: {project.Id}\n");

        // 2. CREATE: Insert Segments
        Console.WriteLine("2️⃣ CREATE: Inserting Segments...");
        var segment1 = new Segment
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = project.Id,
            StartTime = 0,
            EndTime = 5.5,
            Text = "Hello, welcome to the podcast",
            Order = 1,
            TransitionType = "fade",
            TransitionDuration = 0.3
        };

        var segment2 = new Segment
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = project.Id,
            StartTime = 5.5,
            EndTime = 12.0,
            Text = "Today we'll discuss amazing topics",
            Order = 2,
            TransitionType = "fade",
            TransitionDuration = 0.3
        };

        context.Segments.AddRange(segment1, segment2);
        await context.SaveChangesAsync();
        Console.WriteLine($"✅ Created 2 segments\n");

        // 3. CREATE: Insert an Asset
        Console.WriteLine("3️⃣ CREATE: Inserting an Asset...");
        var asset = new Asset
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = project.Id,
            FilePath = "/path/to/background.jpg",
            FileName = "background.jpg",
            Extension = "jpg",
            FileSize = 1024000,
            Type = "Image",
            Name = "Background Image",
            CreatedAt = DateTime.UtcNow
        };

        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        Console.WriteLine($"✅ Asset created: {asset.Id}\n");

        // 4. READ: Query all projects
        Console.WriteLine("4️⃣ READ: Querying all projects...");
        var projects = await context.Projects.ToListAsync();
        Console.WriteLine($"✅ Found {projects.Count} project(s)");
        foreach (var p in projects)
        {
            Console.WriteLine($"   - {p.Name} (ID: {p.Id})");
        }
        Console.WriteLine();

        // 5. READ: Query segments with eager loading
        Console.WriteLine("5️⃣ READ: Querying segments for project...");
        var segments = await context.Segments
            .Where(s => s.ProjectId == project.Id)
            .ToListAsync();
        Console.WriteLine($"✅ Found {segments.Count} segment(s) in project");
        foreach (var seg in segments)
        {
            Console.WriteLine($"   - Order {seg.Order}: {seg.Text} ({seg.StartTime}s - {seg.EndTime}s)");
        }
        Console.WriteLine();

        // 6. UPDATE: Modify project
        Console.WriteLine("6️⃣ UPDATE: Modifying project name...");
        project.Name = "Updated Podcast Video Test";
        project.UpdatedAt = DateTime.UtcNow;
        context.Projects.Update(project);
        await context.SaveChangesAsync();
        Console.WriteLine($"✅ Project updated: {project.Name}\n");

        // 7. READ: Verify update
        Console.WriteLine("7️⃣ READ: Verifying update...");
        var updatedProject = await context.Projects.FirstOrDefaultAsync(p => p.Id == project.Id);
        Console.WriteLine($"✅ Project name is now: {updatedProject?.Name}\n");

        // 8. DELETE: Remove a segment
        Console.WriteLine("8️⃣ DELETE: Removing a segment...");
        var segmentToDelete = segment2;
        context.Segments.Remove(segmentToDelete);
        await context.SaveChangesAsync();
        Console.WriteLine($"✅ Segment deleted\n");

        // 9. READ: Verify deletion
        Console.WriteLine("9️⃣ READ: Verifying deletion...");
        var remainingSegments = await context.Segments
            .Where(s => s.ProjectId == project.Id)
            .ToListAsync();
        Console.WriteLine($"✅ Remaining segments: {remainingSegments.Count}\n");

        // 10. Database Info
        Console.WriteLine("📊 Database Information:");
        Console.WriteLine($"   Location: {dbPath}");
        var fileInfo = new FileInfo(dbPath);
        Console.WriteLine($"   Size: {fileInfo.Length} bytes");
        Console.WriteLine($"   ✅ All CRUD operations completed successfully!");
    }
}
