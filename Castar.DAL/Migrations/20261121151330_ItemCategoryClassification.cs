using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261121151330_ItemCategoryClassification : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite accepts an inline nullable foreign key, avoiding a rebuild of the existing item table.
        migrationBuilder.Sql("""
            ALTER TABLE "Items" ADD COLUMN "BudgetCategoryId" TEXT
                CONSTRAINT "FK_Items_BudgetCategories_BudgetCategoryId" REFERENCES "BudgetCategories" ("Id") ON DELETE RESTRICT;
            """);

        // Existing rows read Archived without an UPDATE. New items start Pending in the domain model.
        migrationBuilder.AddColumn<int>(
            name: "CategoryState",
            table: "Items",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "ItemCategoryHistory",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                BudgetCategoryId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ItemCategoryHistory", x => x.Key);
                table.ForeignKey(
                    name: "FK_ItemCategoryHistory_BudgetCategories_BudgetCategoryId",
                    column: x => x.BudgetCategoryId,
                    principalTable: "BudgetCategories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Items_BudgetCategoryId",
            table: "Items",
            column: "BudgetCategoryId");

        migrationBuilder.CreateIndex(
            name: "IX_Items_CategoryState_CreatedOn_Id",
            table: "Items",
            columns: new[] { "CategoryState", "CreatedOn", "Id" },
            filter: "[CategoryState] = 1");

        migrationBuilder.CreateIndex(
            name: "IX_ItemCategoryHistory_BudgetCategoryId",
            table: "ItemCategoryHistory",
            column: "BudgetCategoryId");

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Items_BudgetCategories_BudgetCategoryId",
            table: "Items");

        migrationBuilder.DropTable(
            name: "ItemCategoryHistory");

        migrationBuilder.DropIndex(
            name: "IX_Items_BudgetCategoryId",
            table: "Items");

        migrationBuilder.DropIndex(
            name: "IX_Items_CategoryState_CreatedOn_Id",
            table: "Items");

        migrationBuilder.DropColumn(
            name: "BudgetCategoryId",
            table: "Items");

        migrationBuilder.DropColumn(
            name: "CategoryState",
            table: "Items");
    }
}
