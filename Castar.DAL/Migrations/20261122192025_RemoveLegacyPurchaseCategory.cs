using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261122192025_RemoveLegacyPurchaseCategory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Purchases_CategoryId",
            table: "Purchases");

        // The foreign key is declared inline, so drop only this column without rebuilding Purchases.
        migrationBuilder.Sql("""ALTER TABLE "Purchases" DROP COLUMN "CategoryId";""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Purchases" ADD COLUMN "CategoryId" TEXT
                CONSTRAINT "FK_Purchases_BudgetCategories_CategoryId" REFERENCES "BudgetCategories" ("Id") ON DELETE RESTRICT;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_CategoryId",
            table: "Purchases",
            column: "CategoryId");
    }
}
