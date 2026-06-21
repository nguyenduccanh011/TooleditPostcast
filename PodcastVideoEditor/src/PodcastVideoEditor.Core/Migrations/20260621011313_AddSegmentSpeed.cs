using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Speed",
                table: "Segments",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Speed",
                table: "Segments");
        }
    }
}
