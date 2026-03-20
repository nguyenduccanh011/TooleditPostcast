using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentSourceStartOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SourceStartOffset",
                table: "Segments",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceStartOffset",
                table: "Segments");
        }
    }
}
