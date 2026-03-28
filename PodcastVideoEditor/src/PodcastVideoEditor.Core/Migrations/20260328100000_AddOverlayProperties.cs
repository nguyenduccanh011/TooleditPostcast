using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOverlayProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Track: default overlay color and opacity for all segments in the track
            migrationBuilder.AddColumn<string>(
                name: "OverlayColorHex",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: "#000000");

            migrationBuilder.AddColumn<double>(
                name: "OverlayOpacity",
                table: "Tracks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Segment: nullable overlay overrides (null = use track default)
            migrationBuilder.AddColumn<string>(
                name: "OverlayColorHex",
                table: "Segments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OverlayOpacity",
                table: "Segments",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OverlayColorHex",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "OverlayOpacity",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "OverlayColorHex",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "OverlayOpacity",
                table: "Segments");
        }
    }
}
