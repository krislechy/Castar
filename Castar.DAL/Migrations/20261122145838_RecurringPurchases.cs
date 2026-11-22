using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261122145838_RecurringPurchases : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RecurringPurchases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                ScheduleJson = table.Column<string>(type: "TEXT", nullable: false),
                IsPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                NextOccurrence = table.Column<int>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecurringPurchases", x => x.Id);
                table.ForeignKey(
                    name: "FK_RecurringPurchases_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RecurringPurchases_IsPaused_NextRunUtc_Id",
            table: "RecurringPurchases",
            columns: new[] { "IsPaused", "NextRunUtc", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_RecurringPurchases_UserId",
            table: "RecurringPurchases",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RecurringPurchases");
    }
}
