using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261116152525_PurchaseTelegramOrigin : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Purchases_UserId_MessageId",
            table: "Purchases");

        migrationBuilder.AddColumn<Guid>(
            name: "TelegramOriginUserId",
            table: "Purchases",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "Purchases"
            SET "TelegramOriginUserId" = "UserId"
            WHERE "MessageId" IS NOT NULL AND "MessageId" <> 0;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_TelegramOriginUserId_MessageId",
            table: "Purchases",
            columns: new[] { "TelegramOriginUserId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_UserId",
            table: "Purchases",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Purchases_TelegramOriginUserId_MessageId",
            table: "Purchases");

        migrationBuilder.DropIndex(
            name: "IX_Purchases_UserId",
            table: "Purchases");

        migrationBuilder.DropColumn(
            name: "TelegramOriginUserId",
            table: "Purchases");

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_UserId_MessageId",
            table: "Purchases",
            columns: new[] { "UserId", "MessageId" });
    }
}
