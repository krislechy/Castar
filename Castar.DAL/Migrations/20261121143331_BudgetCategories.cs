using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261121143331_BudgetCategories : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BudgetCategories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BudgetCategories", x => x.Id);
            });

        // SQLite can add a nullable inline foreign key without rebuilding the operation table.
        migrationBuilder.Sql("""
            ALTER TABLE "Purchases" ADD COLUMN "CategoryId" TEXT
                CONSTRAINT "FK_Purchases_BudgetCategories_CategoryId" REFERENCES "BudgetCategories" ("Id") ON DELETE RESTRICT;
            ALTER TABLE "Incomes" ADD COLUMN "CategoryId" TEXT
                CONSTRAINT "FK_Incomes_BudgetCategories_CategoryId" REFERENCES "BudgetCategories" ("Id") ON DELETE RESTRICT;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_CategoryId",
            table: "Purchases",
            column: "CategoryId");

        migrationBuilder.CreateIndex(
            name: "IX_Incomes_CategoryId",
            table: "Incomes",
            column: "CategoryId");

        migrationBuilder.CreateIndex(
            name: "IX_BudgetCategories_Kind_NormalizedName",
            table: "BudgetCategories",
            columns: new[] { "Kind", "NormalizedName" },
            unique: true);

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Incomes_BudgetCategories_CategoryId",
            table: "Incomes");

        migrationBuilder.DropForeignKey(
            name: "FK_Purchases_BudgetCategories_CategoryId",
            table: "Purchases");

        migrationBuilder.DropTable(
            name: "BudgetCategories");

        migrationBuilder.DropIndex(
            name: "IX_Purchases_CategoryId",
            table: "Purchases");

        migrationBuilder.DropIndex(
            name: "IX_Incomes_CategoryId",
            table: "Incomes");

        migrationBuilder.DropColumn(
            name: "CategoryId",
            table: "Purchases");

        migrationBuilder.DropColumn(
            name: "CategoryId",
            table: "Incomes");
    }
}
