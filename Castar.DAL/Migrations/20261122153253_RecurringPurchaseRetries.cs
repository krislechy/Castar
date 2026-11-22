using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261122153253_RecurringPurchaseRetries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "RetryAfterUtc",
            table: "RecurringPurchases",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RetryCount",
            table: "RecurringPurchases",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RetryAfterUtc",
            table: "RecurringPurchases");

        migrationBuilder.DropColumn(
            name: "RetryCount",
            table: "RecurringPurchases");
    }
}
