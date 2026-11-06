using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomLayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegendEntryId",
                table: "MapFeatures",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MapLegendEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapLayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Shape = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapLegendEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapLegendEntries_MapLayers_MapLayerId",
                        column: x => x.MapLayerId,
                        principalTable: "MapLayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapFeatures_LegendEntryId",
                table: "MapFeatures",
                column: "LegendEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLegendEntries_MapLayerId",
                table: "MapLegendEntries",
                column: "MapLayerId");

            migrationBuilder.AddForeignKey(
                name: "FK_MapFeatures_MapLegendEntries_LegendEntryId",
                table: "MapFeatures",
                column: "LegendEntryId",
                principalTable: "MapLegendEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MapFeatures_MapLegendEntries_LegendEntryId",
                table: "MapFeatures");

            migrationBuilder.DropTable(
                name: "MapLegendEntries");

            migrationBuilder.DropIndex(
                name: "IX_MapFeatures_LegendEntryId",
                table: "MapFeatures");

            migrationBuilder.DropColumn(
                name: "LegendEntryId",
                table: "MapFeatures");
        }
    }
}
