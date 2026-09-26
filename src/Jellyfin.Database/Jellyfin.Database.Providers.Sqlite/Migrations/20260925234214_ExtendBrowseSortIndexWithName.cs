using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ExtendBrowseSortIndexWithName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName_Name",
                table: "BaseItems",
                columns: new[] { "Type", "TopParentId", "SortName", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName_Name",
                table: "BaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName",
                table: "BaseItems",
                columns: new[] { "Type", "TopParentId", "SortName" });
        }
    }
}
