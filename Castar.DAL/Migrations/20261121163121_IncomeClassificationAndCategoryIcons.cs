using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261121163121_IncomeClassificationAndCategoryIcons : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "CategoryRevision",
            table: "Incomes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "CategoryState",
            table: "Incomes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "IconKey",
            table: "BudgetCategories",
            type: "TEXT",
            maxLength: 24,
            nullable: true);

        migrationBuilder.Sql("""UPDATE "Incomes" SET "CategoryState" = 3 WHERE "CategoryId" IS NOT NULL""");

        migrationBuilder.CreateTable(
            name: "IncomeCategoryHistory",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CategoryId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IncomeCategoryHistory", x => x.Key);
                table.ForeignKey(
                    name: "FK_IncomeCategoryHistory_BudgetCategories_CategoryId",
                    column: x => x.CategoryId,
                    principalTable: "BudgetCategories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Incomes_CategoryState_CreatedOn_Id",
            table: "Incomes",
            columns: new[] { "CategoryState", "CreatedOn", "Id" },
            filter: "[CategoryState] = 1");

        migrationBuilder.CreateIndex(
            name: "IX_IncomeCategoryHistory_CategoryId",
            table: "IncomeCategoryHistory",
            column: "CategoryId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IncomeCategoryHistory");

        migrationBuilder.DropIndex(
            name: "IX_Incomes_CategoryState_CreatedOn_Id",
            table: "Incomes");

        migrationBuilder.DropColumn(
            name: "CategoryRevision",
            table: "Incomes");

        migrationBuilder.DropColumn(
            name: "CategoryState",
            table: "Incomes");

        migrationBuilder.DropColumn(
            name: "IconKey",
            table: "BudgetCategories");
    }
}
