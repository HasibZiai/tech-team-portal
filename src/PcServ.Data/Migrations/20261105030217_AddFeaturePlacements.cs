using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeaturePlacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MapFeaturePlacements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapLinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    MapFeatureId = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapFeaturePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapFeaturePlacements_MapFeatures_MapFeatureId",
                        column: x => x.MapFeatureId,
                        principalTable: "MapFeatures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapFeaturePlacements_MapLinks_MapLinkId",
                        column: x => x.MapLinkId,
                        principalTable: "MapLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapFeaturePlacements_MapFeatureId",
                table: "MapFeaturePlacements",
                column: "MapFeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MapFeaturePlacements_MapLinkId_MapFeatureId",
                table: "MapFeaturePlacements",
                columns: new[] { "MapLinkId", "MapFeatureId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapFeaturePlacements");
        }
    }
}
