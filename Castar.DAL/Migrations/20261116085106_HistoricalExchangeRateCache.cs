using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261116085106_HistoricalExchangeRateCache : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExchangeRates",
            columns: table => new
            {
                Source = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                Target = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                QuotedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Rate = table.Column<decimal>(type: "TEXT", nullable: false),
                RetrievedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExchangeRates", x => new { x.Target, x.Source, x.Date });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ExchangeRates");
    }
}
