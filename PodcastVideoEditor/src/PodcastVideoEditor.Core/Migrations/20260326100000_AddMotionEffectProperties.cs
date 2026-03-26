using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMotionEffectProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Segment: motion preset and intensity for Ken Burns effects
            migrationBuilder.AddColumn<string>(
                name: "MotionPreset",
                table: "Segments",
                type: "TEXT",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<double>(
                name: "MotionIntensity",
                table: "Segments",
                type: "REAL",
                nullable: true);

            // Track: auto-motion toggle and default intensity
            migrationBuilder.AddColumn<bool>(
                name: "AutoMotionEnabled",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MotionIntensity",
                table: "Tracks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MotionPreset",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "MotionIntensity",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "AutoMotionEnabled",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MotionIntensity",
                table: "Tracks");
        }
    }
}
