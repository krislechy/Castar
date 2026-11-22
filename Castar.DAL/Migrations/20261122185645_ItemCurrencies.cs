using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261122185645_ItemCurrencies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Currency",
            table: "Items",
            type: "TEXT",
            maxLength: 3,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Currency",
            table: "Items");
    }
}
