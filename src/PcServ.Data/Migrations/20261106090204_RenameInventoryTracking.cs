using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameInventoryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DestinyUpdatedTo",
                table: "Repairs",
                newName: "InventoryUpdatedTo");

            migrationBuilder.RenameColumn(
                name: "DestinyUpdatedOn",
                table: "Repairs",
                newName: "InventoryUpdatedOn");

            migrationBuilder.RenameColumn(
                name: "DestinyUpdatedBy",
                table: "Repairs",
                newName: "InventoryUpdatedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "InventoryUpdatedTo",
                table: "Repairs",
                newName: "DestinyUpdatedTo");

            migrationBuilder.RenameColumn(
                name: "InventoryUpdatedOn",
                table: "Repairs",
                newName: "DestinyUpdatedOn");

            migrationBuilder.RenameColumn(
                name: "InventoryUpdatedBy",
                table: "Repairs",
                newName: "DestinyUpdatedBy");
        }
    }
}
