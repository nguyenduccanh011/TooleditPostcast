#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PodcastVideoEditor.Core.Models;

namespace PodcastVideoEditor.Core.Database;

/// <summary>
/// Entity Framework Core DbContext for Podcast Video Editor
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Design-time factory for EF Core migrations
    /// </summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodcastVideoEditor", "app.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            optionsBuilder.UseSqlite($"Data Source={dbPath}");

            return new AppDbContext(optionsBuilder.Options);
        }
    }

    /// <summary>
    /// Projects table
    /// </summary>
    public DbSet<Project> Projects { get; set; } = null!;

    /// <summary>
    /// Segments table
    /// </summary>
    public DbSet<Segment> Segments { get; set; } = null!;

    /// <summary>
    /// Tracks table (multi-track timeline support)
    /// </summary>
    public DbSet<Track> Tracks { get; set; } = null!;

    /// <summary>
    /// Elements table
    /// </summary>
    public DbSet<Element> Elements { get; set; } = null!;

    /// <summary>
    /// Assets table
    /// </summary>
    public DbSet<Asset> Assets { get; set; } = null!;

    /// <summary>
    /// Background music tracks table
    /// </summary>
    public DbSet<BgmTrack> BgmTracks { get; set; } = null!;

    /// <summary>
    /// Templates table
    /// </summary>
    public DbSet<Template> Templates { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configure RenderSettings as owned type (embedded in Project)
        builder.Entity<Project>()
            .OwnsOne(p => p.RenderSettings);

        // Configure Project relationships
        builder.Entity<Project>()
            .HasMany(p => p.Tracks)
            .WithOne(t => t.Project)
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Project>()
            .HasMany(p => p.Segments)
            .WithOne(s => s.Project)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Track-Segment relationship (multi-track support)
        builder.Entity<Track>()
            .HasMany(t => t.Segments)
            .WithOne(s => s.Track)
            .HasForeignKey(s => s.TrackId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Project>()
            .HasMany(p => p.Elements)
            .WithOne(e => e.Project)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Project>()
            .HasMany(p => p.Assets)
            .WithOne(a => a.Project)
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Project>()
            .HasMany(p => p.BgmTracks)
            .WithOne(b => b.Project)
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for performance
        builder.Entity<Track>()
            .HasIndex(t => t.ProjectId)
            .HasDatabaseName("IX_Track_ProjectId");

        builder.Entity<Segment>()
            .HasIndex(s => s.ProjectId)
            .HasDatabaseName("IX_Segment_ProjectId");

        builder.Entity<Segment>()
            .HasIndex(s => s.TrackId)
            .HasDatabaseName("IX_Segment_TrackId");

        builder.Entity<Element>()
            .HasIndex(e => e.ProjectId)
            .HasDatabaseName("IX_Element_ProjectId");

        builder.Entity<Element>()
            .HasIndex(e => e.SegmentId)
            .HasDatabaseName("IX_Element_SegmentId");

        builder.Entity<Element>()
            .HasOne(e => e.Segment)
            .WithMany()
            .HasForeignKey(e => e.SegmentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<Asset>()
            .HasIndex(a => a.ProjectId)
            .HasDatabaseName("IX_Asset_ProjectId");

        builder.Entity<BgmTrack>()
            .HasIndex(b => b.ProjectId)
            .HasDatabaseName("IX_BgmTrack_ProjectId");
    }
}
