using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261122191042_MovePurchaseAmountsToItems : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PurchaseAdjustments",
            columns: table => new
            {
                PurchaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                Amount = table.Column<decimal>(type: "TEXT", precision: 24, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseAdjustments", x => x.PurchaseId);
                table.ForeignKey(
                    name: "FK_PurchaseAdjustments_Purchases_PurchaseId",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("""
            WITH LegacyTotals AS (
                SELECT p.Id, p.Currency, p.TotalSum,
                    COALESCE((
                        SELECT ef_sum(i.Sum) FROM Items i
                        WHERE i.PurchaseId = p.Id AND COALESCE(i.Currency, p.Currency) IS p.Currency
                    ), '0.0') AS InCurrency,
                    EXISTS (SELECT 1 FROM Items i WHERE i.PurchaseId = p.Id) AS HasItems
                FROM Purchases p
            )
            INSERT INTO PurchaseAdjustments (PurchaseId, Currency, Amount)
            SELECT Id, Currency, ef_add(TotalSum, ef_negate(InCurrency))
            FROM LegacyTotals WHERE NOT HasItems OR ef_compare(TotalSum, InCurrency) <> 0;
            """);

        migrationBuilder.Sql("""
            UPDATE Items
            SET Currency = (SELECT p.Currency FROM Purchases p WHERE p.Id = Items.PurchaseId)
            WHERE Currency IS NULL;
            """);
        // Drop only the replaced columns while preserving every existing purchase row and its foreign keys.
        migrationBuilder.Sql("""ALTER TABLE "Purchases" DROP COLUMN "Currency";""");
        migrationBuilder.Sql("""ALTER TABLE "Purchases" DROP COLUMN "TotalSum";""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The previous schema can only represent purchases whose amounts have one currency.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE CastarOneCurrencyOnly (Valid INTEGER NOT NULL CHECK (Valid = 1));
            INSERT INTO CastarOneCurrencyOnly
            SELECT NOT EXISTS (
                SELECT PurchaseId FROM (
                    SELECT PurchaseId, Currency FROM Items
                    UNION ALL
                    SELECT PurchaseId, Currency FROM PurchaseAdjustments
                    WHERE ef_compare(Amount, '0.0') <> 0
                )
                GROUP BY PurchaseId
                HAVING COUNT(DISTINCT COALESCE(Currency, '')) > 1
            );
            DROP TABLE CastarOneCurrencyOnly;
            """);
        migrationBuilder.AddColumn<string>(
            name: "Currency",
            table: "Purchases",
            type: "TEXT",
            maxLength: 3,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "TotalSum",
            table: "Purchases",
            type: "TEXT",
            precision: 24,
            scale: 2,
            nullable: false,
            defaultValue: 0m);
        migrationBuilder.Sql("""
            UPDATE Purchases
            SET Currency = COALESCE((
                    SELECT i.Currency FROM Items i WHERE i.PurchaseId = Purchases.Id LIMIT 1
                ), (SELECT a.Currency FROM PurchaseAdjustments a WHERE a.PurchaseId = Purchases.Id)),
                TotalSum = ef_add(
                    COALESCE((SELECT ef_sum(i.Sum) FROM Items i WHERE i.PurchaseId = Purchases.Id), '0.0'),
                    COALESCE((SELECT a.Amount FROM PurchaseAdjustments a WHERE a.PurchaseId = Purchases.Id), '0.0')
                );
            """);
        migrationBuilder.DropTable(name: "PurchaseAdjustments");
    }
}
