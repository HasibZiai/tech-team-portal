using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "MapFeatures",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MapPhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapFeatureId = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapPhotos_MapFeatures_MapFeatureId",
                        column: x => x.MapFeatureId,
                        principalTable: "MapFeatures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapPhotos_MapFeatureId",
                table: "MapPhotos",
                column: "MapFeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_MapPhotos_StorageKey",
                table: "MapPhotos",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapPhotos");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "MapFeatures");
        }
    }
}
