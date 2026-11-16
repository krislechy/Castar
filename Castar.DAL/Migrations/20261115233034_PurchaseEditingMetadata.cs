using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261115233034_PurchaseEditingMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OriginalReceiptSnapshot",
            table: "Purchases",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReceiptSource",
            table: "Purchases",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Purchases",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OriginalReceiptSnapshot",
            table: "Purchases");

        migrationBuilder.DropColumn(
            name: "ReceiptSource",
            table: "Purchases");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "Purchases");
    }
}
