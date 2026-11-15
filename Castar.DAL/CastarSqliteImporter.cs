using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL
{
    public static class CastarSqliteImporter
    {
        public sealed record ImportResult(long Users, long Purchases, long Items, long Incomes);

        public static async Task<ImportResult> ImportAsync(
            string sqlServerConnectionString, string sqlitePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlServerConnectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
            var targetPath = Path.GetFullPath(sqlitePath);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
                throw new IOException("Для переноса укажите новый файл SQLite.");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.import";
            try
            {
                var result = await CopyDatabaseAsync(sqlServerConnectionString, temporaryPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, targetPath);
                return result;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static async Task<ImportResult> CopyDatabaseAsync(
            string sqlServerConnectionString, string temporaryPath, CancellationToken cancellationToken)
        {
            await using var source = new SqlConnection(sqlServerConnectionString);
            await source.OpenAsync(cancellationToken);
            await using var sourceTransaction = (SqlTransaction)await source.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var schema = await ReadSchemaAsync(source, sourceTransaction, cancellationToken);
            var sqliteConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                ForeignKeys = true,
                Pooling = false
            }.ToString();
            var options = new DbContextOptionsBuilder<CastarDbContext>().UseSqlite(sqliteConnectionString).Options;
            await using (var db = new CastarDbContext(options))
                await db.Database.EnsureCreatedAsync(cancellationToken);

            await using var destination = new SqliteConnection(sqliteConnectionString);
            await destination.OpenAsync(cancellationToken);
            await using var destinationTransaction = (SqliteTransaction)await destination.BeginTransactionAsync(cancellationToken);

            var users = await CopyTableAsync(source, sourceTransaction, destination, destinationTransaction, "Users",
                "Id,TelegramId,Name,Sex,Color,Image,CreatedOn,ModifiedOn", cancellationToken);
            var purchases = await CopyTableAsync(source, sourceTransaction, destination, destinationTransaction, "Purchases",
                "Id,UserId,Name,TotalSum,MessageId,AddedOn,CreatedOn,ModifiedOn,RetailPlace,RetailPlaceAddress,RawData",
                cancellationToken, schema.ReceiptKey ? "ReceiptKey" : null, schema.Currency ? "Currency" : null);
            var items = await CopyTableAsync(source, sourceTransaction, destination, destinationTransaction, "Items",
                "Id,PurchaseId,Name,Category,Quantity,Price,Sum,CreatedOn,ModifiedOn", cancellationToken,
                schema.ItemLineNumber ? "LineNumber" : null);
            var incomes = await CopyTableAsync(source, sourceTransaction, destination, destinationTransaction, "Incomes",
                "Id,UserId,MessageId,Sum,Source,AddedOn,CreatedOn,ModifiedOn", cancellationToken,
                schema.IncomeLineNumber ? "LineNumber" : null);

            await sourceTransaction.CommitAsync(cancellationToken);
            await destinationTransaction.CommitAsync(cancellationToken);
            return new ImportResult(users, purchases, items, incomes);
        }

        private sealed record SourceSchema(bool ReceiptKey, bool Currency, bool ItemLineNumber, bool IncomeLineNumber);

        private static async Task<SourceSchema> ReadSchemaAsync(
            SqlConnection source, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            const string sql = """
                SELECT
                    CASE WHEN COL_LENGTH('dbo.Users', 'Color') = 4
                          AND COL_LENGTH('dbo.Purchases', 'RawData') = 100
                          AND COL_LENGTH('dbo.Incomes', 'UserId') = 16
                          AND COL_LENGTH('dbo.Items', 'PurchaseId') = 16
                         THEN 1 ELSE 0 END,
                    CASE WHEN COL_LENGTH('dbo.Purchases', 'ReceiptKey') = 32 THEN 1 ELSE 0 END,
                    CASE WHEN COL_LENGTH('dbo.Purchases', 'Currency') = 3 THEN 1 ELSE 0 END,
                    CASE WHEN COL_LENGTH('dbo.Items', 'LineNumber') = 4 THEN 1 ELSE 0 END,
                    CASE WHEN COL_LENGTH('dbo.Incomes', 'LineNumber') = 4 THEN 1 ELSE 0 END;
                """;

            await using var command = new SqlCommand(sql, source, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1)
                throw new InvalidOperationException("Поддерживается схема Castar из DataBase.zip или castardb30072022.sql.");

            return new SourceSchema(reader.GetInt32(1) == 1, reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1, reader.GetInt32(4) == 1);
        }

        private static async Task<long> CopyTableAsync(
            SqlConnection source, SqlTransaction sourceTransaction,
            SqliteConnection destination, SqliteTransaction destinationTransaction,
            string table, string requiredColumns, CancellationToken cancellationToken, params string?[] optionalColumns)
        {
            // Names come only from the fixed application schema above.
            var columns = requiredColumns.Split(',').Concat(optionalColumns.OfType<string>()).ToArray();
            var identifiers = string.Join(", ", columns.Select(column => "[" + column + "]"));
            var valueNames = columns.Select((_, index) => "$p" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            await using var select = new SqlCommand($"SELECT {identifiers} FROM [dbo].[{table}];", source, sourceTransaction);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            await using var insert = destination.CreateCommand();
            insert.Transaction = destinationTransaction;
            insert.CommandText = $"INSERT INTO [{table}] ({identifiers}) VALUES ({string.Join(", ", valueNames)});";
            foreach (var name in valueNames)
                insert.Parameters.AddWithValue(name, DBNull.Value);

            long rows = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < columns.Length; index++)
                    insert.Parameters[index].Value = reader.GetValue(index);

                rows += await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return rows;
        }
    }
}
