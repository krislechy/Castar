using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261115223016_OriginalCurrencyAndReceiptDate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "ReceiptDate",
            table: "Purchases",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Currency",
            table: "Incomes",
            type: "TEXT",
            maxLength: 3,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReceiptDate",
            table: "Purchases");

        migrationBuilder.DropColumn(
            name: "Currency",
            table: "Incomes");
    }
}
