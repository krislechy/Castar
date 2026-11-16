using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261116174952_PersistReceiptRetryQueue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "QueuedReceipts",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                AcknowledgementMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                RetryAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ScheduledRetryMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                Result = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QueuedReceipts", x => x.Key);
            });

        migrationBuilder.CreateIndex(
            name: "IX_QueuedReceipts_NextAttemptUtc",
            table: "QueuedReceipts",
            column: "NextAttemptUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "QueuedReceipts");
    }
}
