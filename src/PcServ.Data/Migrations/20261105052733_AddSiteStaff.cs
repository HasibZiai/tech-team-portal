using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Contact",
                table: "OrderStatusChanges",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Contact",
                table: "Orders",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SiteStaffMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    JobTitle = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Room = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsOrderContact = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteStaffMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteStaffMembers_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteStaffMembers_SiteId",
                table: "SiteStaffMembers",
                column: "SiteId");

            // The stage now names the person, so existing data moves across before the old field is dropped.
            migrationBuilder.Sql("UPDATE Orders SET Status = 'SentToContact' WHERE Status = 'SentToFinance';");
            migrationBuilder.Sql("UPDATE OrderStatusChanges SET Status = 'SentToContact' WHERE Status = 'SentToFinance';");
            migrationBuilder.Sql("UPDATE Orders SET Contact = (SELECT TRIM(FinanceContact) FROM Sites WHERE Sites.Id = Orders.SiteId AND TRIM(FinanceContact) <> '');");
            migrationBuilder.Sql("UPDATE OrderStatusChanges SET Contact = (SELECT Contact FROM Orders WHERE Orders.Id = OrderStatusChanges.OrderId) WHERE Status = 'SentToContact';");
            migrationBuilder.Sql("INSERT INTO SiteStaffMembers (SiteId, Name, IsOrderContact, CreatedAt, CreatedBy) SELECT Id, TRIM(FinanceContact), 1, CURRENT_TIMESTAMP, 'system' FROM Sites WHERE TRIM(FinanceContact) <> '';");

            migrationBuilder.DropColumn(
                name: "FinanceContact",
                table: "Sites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Orders SET Status = 'SentToFinance' WHERE Status = 'SentToContact';");
            migrationBuilder.Sql("UPDATE OrderStatusChanges SET Status = 'SentToFinance' WHERE Status = 'SentToContact';");

            migrationBuilder.DropTable(
                name: "SiteStaffMembers");

            migrationBuilder.DropColumn(
                name: "Contact",
                table: "OrderStatusChanges");

            migrationBuilder.DropColumn(
                name: "Contact",
                table: "Orders");

            migrationBuilder.AddColumn<string>(
                name: "FinanceContact",
                table: "Sites",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }
    }
}
