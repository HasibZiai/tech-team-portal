using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureNeedsReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "MapFeatures",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "MapFeatures");
        }
    }
}
