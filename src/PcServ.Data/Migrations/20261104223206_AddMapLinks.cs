using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MapLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceMapId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetMapId = table.Column<int>(type: "INTEGER", nullable: false),
                    M00 = table.Column<double>(type: "REAL", nullable: false),
                    M01 = table.Column<double>(type: "REAL", nullable: false),
                    M02 = table.Column<double>(type: "REAL", nullable: false),
                    M10 = table.Column<double>(type: "REAL", nullable: false),
                    M11 = table.Column<double>(type: "REAL", nullable: false),
                    M12 = table.Column<double>(type: "REAL", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapLinks_SiteMaps_SourceMapId",
                        column: x => x.SourceMapId,
                        principalTable: "SiteMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapLinks_SiteMaps_TargetMapId",
                        column: x => x.TargetMapId,
                        principalTable: "SiteMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_SourceMapId_TargetMapId",
                table: "MapLinks",
                columns: new[] { "SourceMapId", "TargetMapId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_TargetMapId",
                table: "MapLinks",
                column: "TargetMapId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapLinks");
        }
    }
}
