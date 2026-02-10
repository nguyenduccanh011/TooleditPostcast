using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Segments",
                type: "TEXT",
                nullable: false,
                defaultValue: "visual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Segments");
        }
    }
}
