#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Database;

/// <summary>
/// Separate DbContext for the global asset library (library.db).
/// Keeps library data independent of per-project app.db.
/// </summary>
public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options)
    {
    }

    public DbSet<GlobalAsset> GlobalAssets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<GlobalAsset>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Category).HasDatabaseName("IX_GlobalAsset_Category");
            e.HasIndex(a => a.IsBuiltIn).HasDatabaseName("IX_GlobalAsset_IsBuiltIn");
        });
    }

    /// <summary>
    /// Design-time factory for EF Core migrations targeting library.db.
    /// </summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
    {
        public LibraryDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<LibraryDbContext>();
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PodcastVideoEditor", "library.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
            return new LibraryDbContext(optionsBuilder.Options);
        }
    }
}
