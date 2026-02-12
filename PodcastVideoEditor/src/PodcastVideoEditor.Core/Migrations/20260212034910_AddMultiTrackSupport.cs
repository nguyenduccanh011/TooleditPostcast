using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTrackSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TrackId",
                table: "Segments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackType = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Segment_TrackId",
                table: "Segments",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Track_ProjectId",
                table: "Tracks",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Segments_Tracks_TrackId",
                table: "Segments",
                column: "TrackId",
                principalTable: "Tracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Data migration: Create default tracks for each project
            migrationBuilder.Sql(@"
                -- Create default tracks for each project
                INSERT INTO Tracks (Id, ProjectId, [Order], TrackType, [Name], IsLocked, IsVisible)
                SELECT 
                    lower(hex(randomblob(16))),
                    p.Id,
                    0,
                    'text',
                    'Text 1',
                    0,
                    1
                FROM Projects p;

                INSERT INTO Tracks (Id, ProjectId, [Order], TrackType, [Name], IsLocked, IsVisible)
                SELECT 
                    lower(hex(randomblob(16))),
                    p.Id,
                    1,
                    'visual',
                    'Visual 1',
                    0,
                    1
                FROM Projects p;

                INSERT INTO Tracks (Id, ProjectId, [Order], TrackType, [Name], IsLocked, IsVisible)
                SELECT 
                    lower(hex(randomblob(16))),
                    p.Id,
                    2,
                    'audio',
                    'Audio',
                    0,
                    1
                FROM Projects p;
            ", suppressTransaction: false);

            // Data migration: Assign existing segments to Visual 1 track
            migrationBuilder.Sql(@"
                UPDATE Segments
                SET TrackId = (
                    SELECT Id FROM Tracks 
                    WHERE TrackType = 'visual' 
                    AND [Name] = 'Visual 1'
                    AND ProjectId = Segments.ProjectId
                )
                WHERE TrackId IS NULL;
            ", suppressTransaction: false);

            // Alter Segments.TrackId to NOT NULL
            migrationBuilder.AlterColumn<string>(
                name: "TrackId",
                table: "Segments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TrackId",
                table: "Segments",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.DropForeignKey(
                name: "FK_Segments_Tracks_TrackId",
                table: "Segments");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Segment_TrackId",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "TrackId",
                table: "Segments");
        }
    }
}
