using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceDeprovisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeprovisionedBy",
                table: "Devices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DeprovisionedOn",
                table: "Devices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeprovisionedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DeprovisionedOn",
                table: "Devices");
        }
    }
}
