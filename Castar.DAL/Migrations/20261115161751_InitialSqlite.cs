using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261115161751_InitialSqlite : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TelegramId = table.Column<long>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Sex = table.Column<int>(type: "INTEGER", nullable: false),
                Color = table.Column<byte[]>(type: "BLOB", nullable: false),
                Image = table.Column<byte[]>(type: "BLOB", nullable: true),
                CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Incomes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Sum = table.Column<decimal>(type: "TEXT", precision: 24, scale: 2, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                AddedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                LineNumber = table.Column<int>(type: "INTEGER", nullable: true),
                MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Incomes", x => x.Id);
                table.ForeignKey(
                    name: "FK_Incomes_Users",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "Purchases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                TotalSum = table.Column<decimal>(type: "TEXT", precision: 24, scale: 2, nullable: false),
                RetailPlace = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                RetailPlaceAddress = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                RawData = table.Column<byte[]>(type: "BLOB", nullable: true),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                AddedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ReceiptKey = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Purchases", x => x.Id);
                table.ForeignKey(
                    name: "FK_Purchases_Users",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "Items",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PurchaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                Category = table.Column<int>(type: "INTEGER", nullable: true),
                Quantity = table.Column<double>(type: "REAL", nullable: false),
                Price = table.Column<decimal>(type: "TEXT", precision: 24, scale: 2, nullable: false),
                Sum = table.Column<decimal>(type: "TEXT", precision: 24, scale: 2, nullable: false),
                LineNumber = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Items", x => x.Id);
                table.ForeignKey(
                    name: "FK_Items_Purchases",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Incomes_UserId_MessageId",
            table: "Incomes",
            columns: new[] { "UserId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "IX_Items_PurchaseId",
            table: "Items",
            column: "PurchaseId");

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_UserId_MessageId",
            table: "Purchases",
            columns: new[] { "UserId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "UX_Purchases_ReceiptKey",
            table: "Purchases",
            column: "ReceiptKey",
            unique: true,
            filter: "[ReceiptKey] IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Incomes");

        migrationBuilder.DropTable(
            name: "Items");

        migrationBuilder.DropTable(
            name: "Purchases");

        migrationBuilder.DropTable(
            name: "Users");
    }
}
