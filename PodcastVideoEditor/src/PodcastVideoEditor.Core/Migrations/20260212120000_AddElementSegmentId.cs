using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PodcastVideoEditor.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddElementSegmentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SegmentId",
                table: "Elements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Element_SegmentId",
                table: "Elements",
                column: "SegmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Elements_Segments_SegmentId",
                table: "Elements",
                column: "SegmentId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Elements_Segments_SegmentId",
                table: "Elements");

            migrationBuilder.DropIndex(
                name: "IX_Element_SegmentId",
                table: "Elements");

            migrationBuilder.DropColumn(
                name: "SegmentId",
                table: "Elements");
        }
    }
}
