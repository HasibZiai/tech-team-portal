using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcServ.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepairListItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairListItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetTag = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false, collation: "NOCASE"),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true, collation: "NOCASE"),
                    ModelId = table.Column<int>(type: "INTEGER", nullable: true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_RepairListItems_ModelId",
                        column: x => x.ModelId,
                        principalTable: "RepairListItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devices_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Repairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReasonId = table.Column<int>(type: "INTEGER", nullable: true),
                    OtherReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PickupPoint = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DateIn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DateOut = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DestinyUpdatedTo = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    DestinyUpdatedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DestinyUpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FineNeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    FineDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repairs_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Repairs_RepairListItems_ReasonId",
                        column: x => x.ReasonId,
                        principalTable: "RepairListItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Repairs_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AssetTag",
                table: "Devices",
                column: "AssetTag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ModelId",
                table: "Devices",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SerialNumber",
                table: "Devices",
                column: "SerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SiteId",
                table: "Devices",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_DeviceId",
                table: "Repairs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_ReasonId",
                table: "Repairs",
                column: "ReasonId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_SiteId",
                table: "Repairs",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_Status",
                table: "Repairs",
                column: "Status");

            // A starting list of reasons, which Managers can change. Models depend on what each organization owns.
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Broken/cracked screen', 1, 0, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Screen issue/flickering/video cable', 1, 1, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Broken keyboard', 1, 2, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Broken trackpad', 1, 3, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Camera/mic issue', 1, 4, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Wi-Fi issue', 1, 5, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Charging issue', 1, 6, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Won''t power on', 1, 7, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'OS issue', 1, 8, CURRENT_TIMESTAMP, 'system');");
            migrationBuilder.Sql("INSERT INTO RepairListItems (Kind, Name, IsActive, SortOrder, CreatedAt, CreatedBy) VALUES ('Reason', 'Case damage', 1, 9, CURRENT_TIMESTAMP, 'system');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Repairs");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "RepairListItems");
        }
    }
}
