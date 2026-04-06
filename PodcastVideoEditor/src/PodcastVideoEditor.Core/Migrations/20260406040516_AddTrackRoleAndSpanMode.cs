using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackRoleAndSpanMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpanMode",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: "segment_bound");

            migrationBuilder.AddColumn<string>(
                name: "TrackRole",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: "unspecified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpanMode",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "TrackRole",
                table: "Tracks");
        }
    }
}
