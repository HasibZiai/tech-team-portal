using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    BuildingId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Floor = table.Column<int>(type: "INTEGER", nullable: true),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteMaps_Buildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "Buildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SiteMaps_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MapLayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteMapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MinimumRole = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    VisibleByDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapLayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapLayers_SiteMaps_SiteMapId",
                        column: x => x.SiteMapId,
                        principalTable: "SiteMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapFeatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapLayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapFeatures_MapLayers_MapLayerId",
                        column: x => x.MapLayerId,
                        principalTable: "MapLayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapFeatures_MapLayerId",
                table: "MapFeatures",
                column: "MapLayerId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLayers_SiteMapId",
                table: "MapLayers",
                column: "SiteMapId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteMaps_BuildingId",
                table: "SiteMaps",
                column: "BuildingId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteMaps_SiteId",
                table: "SiteMaps",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteMaps_StorageKey",
                table: "SiteMaps",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapFeatures");

            migrationBuilder.DropTable(
                name: "MapLayers");

            migrationBuilder.DropTable(
                name: "SiteMaps");
        }
    }
}
