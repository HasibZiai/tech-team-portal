using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapMinimumRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MinimumRole",
                table: "SiteMaps",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Viewer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinimumRole",
                table: "SiteMaps");
        }
    }
}
