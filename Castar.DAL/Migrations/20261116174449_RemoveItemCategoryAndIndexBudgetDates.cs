using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261116174449_RemoveItemCategoryAndIndexBudgetDates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Category",
            table: "Items");

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_AddedOn",
            table: "Purchases",
            column: "AddedOn");

        migrationBuilder.CreateIndex(
            name: "IX_Incomes_AddedOn",
            table: "Incomes",
            column: "AddedOn");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Purchases_AddedOn",
            table: "Purchases");

        migrationBuilder.DropIndex(
            name: "IX_Incomes_AddedOn",
            table: "Incomes");

        migrationBuilder.AddColumn<int>(
            name: "Category",
            table: "Items",
            type: "INTEGER",
            nullable: true);
    }
}
