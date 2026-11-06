using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WikiImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiImages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WikiSections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiSections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WikiPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumRole = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    SearchText = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PublishedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPages_WikiSections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "WikiSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WikiDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PageId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    BaseVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiDrafts_WikiPages_PageId",
                        column: x => x.PageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPageVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPageVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPageVersions_WikiPages_PageId",
                        column: x => x.PageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiDrafts_PageId_UserName",
                table: "WikiDrafts",
                columns: new[] { "PageId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiImages_StorageKey",
                table: "WikiImages",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_SectionId",
                table: "WikiPages",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPageVersions_PageId_Number",
                table: "WikiPageVersions",
                columns: new[] { "PageId", "Number" },
                unique: true);

            // A section to start with, so Techs can write before a Manager has set anything up.
            migrationBuilder.Sql("INSERT INTO WikiSections (Name, SortOrder, CreatedAt, CreatedBy) VALUES ('General', 0, CURRENT_TIMESTAMP, 'system');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WikiDrafts");

            migrationBuilder.DropTable(
                name: "WikiImages");

            migrationBuilder.DropTable(
                name: "WikiPageVersions");

            migrationBuilder.DropTable(
                name: "WikiPages");

            migrationBuilder.DropTable(
                name: "WikiSections");
        }
    }
}
