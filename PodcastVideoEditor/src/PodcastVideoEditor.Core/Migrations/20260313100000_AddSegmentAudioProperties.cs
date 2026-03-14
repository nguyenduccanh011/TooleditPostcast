using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentAudioProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Volume",
                table: "Segments",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "FadeInDuration",
                table: "Segments",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "FadeOutDuration",
                table: "Segments",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Volume",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "FadeInDuration",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "FadeOutDuration",
                table: "Segments");
        }
    }
}
