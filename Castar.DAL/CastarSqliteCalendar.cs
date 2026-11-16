using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL;

internal static class CastarSqliteCalendar
{
    internal static async Task ConvertLegacyDatesAsync(CastarDbContext db, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) >= 1)
                return;

            var ambiguous = 0;
            foreach (var table in new[] { "Users", "Items", "Incomes", "Purchases" })
                ambiguous += await ConvertTableAsync(connection, transaction, table, token);

            command.CommandText = "PRAGMA user_version = 1";
            await command.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            if (ambiguous > 0)
                Console.WriteLine($"Castar: converted {ambiguous} ambiguous Belgrade timestamps using the earlier instant.");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> ConvertTableAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken token)
    {
        var hasOperationDate = table is "Incomes" or "Purchases";
        var dateColumns = hasOperationDate
            ? table == "Purchases" ? "AddedOn, ReceiptDate" : "AddedOn, NULL"
            : "NULL, NULL";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Id, CreatedOn, ModifiedOn, {dateColumns} FROM {table}";
        var rows = new List<(string Id, DateTime CreatedOn, DateTime? ModifiedOn, DateTime? AddedOn, DateOnly? RateDate)>();
        var ambiguous = 0;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                var localAddedOn = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
                var rateDate = reader.IsDBNull(4)
                    ? localAddedOn is { } local ? DateOnly.FromDateTime(local) : (DateOnly?)null
                    : reader.GetFieldValue<DateOnly>(4);
                rows.Add((id, ConvertDate(reader.GetDateTime(1)),
                    reader.IsDBNull(2) ? null : ConvertDate(reader.GetDateTime(2)),
                    localAddedOn is { } added ? ConvertDate(added) : null, rateDate));

                DateTime ConvertDate(DateTime date)
                {
                    var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    if (BudgetTime.Belgrade.IsInvalidTime(local))
                        throw new InvalidDataException($"Invalid legacy timestamp {local:O} in {table}, id {id}.");
                    if (BudgetTime.Belgrade.IsAmbiguousTime(local))
                        ambiguous++;
                    return BudgetTime.FromLocal(local, BudgetTime.Belgrade);
                }
            }
        }

        var operationUpdate = hasOperationDate ? ", AddedOn = $added, RateDate = $rateDate" : "";
        command.CommandText = $"UPDATE {table} SET CreatedOn = $created, ModifiedOn = $modified{operationUpdate} WHERE Id = $id";
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$created", SqliteType.Text);
        command.Parameters.Add("$modified", SqliteType.Text);
        if (hasOperationDate)
        {
            command.Parameters.Add("$added", SqliteType.Text);
            command.Parameters.Add("$rateDate", SqliteType.Text);
        }
        foreach (var row in rows)
        {
            command.Parameters["$id"].Value = row.Id;
            command.Parameters["$created"].Value = row.CreatedOn;
            command.Parameters["$modified"].Value = (object?)row.ModifiedOn ?? DBNull.Value;
            if (hasOperationDate)
            {
                command.Parameters["$added"].Value = (object?)row.AddedOn ?? DBNull.Value;
                command.Parameters["$rateDate"].Value = (object?)row.RateDate ?? DBNull.Value;
            }
            await command.ExecuteNonQueryAsync(token);
        }
        return ambiguous;
    }
}
