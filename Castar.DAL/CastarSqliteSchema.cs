using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Castar.DAL;

public static class CastarSqliteSchema
{
    public static async Task MigrateAsync(CastarDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Database.CanConnectAsync(cancellationToken)
            && !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            await AdoptLegacySchemaAsync(db, cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);
    }

    private static async Task AdoptLegacySchemaAsync(CastarDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            // Take the write lock before examining the schema so another startup cannot record the baseline twice.
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '__EFMigrationsHistory')";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM __EFMigrationsHistory)";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                    return;
            }

            var actual = await ReadSchemaAsync(command, cancellationToken);
            if (actual.Count == 0)
                return;

            var baselineId = db.GetService<IMigrationsAssembly>().FindMigrationId("InitialSqlite")
                ?? throw new InvalidOperationException("Начальная миграция Castar не найдена.");
            if (!actual.SequenceEqual(await CreateBaselineSchemaAsync(baselineId, cancellationToken)))
            {
                throw new InvalidOperationException(
                    "Схема существующей SQLite не совпадает с исходной схемой Castar. Проверьте путь к базе и её версию.");
            }

            var history = db.GetService<IHistoryRepository>();
            command.CommandText = history.GetCreateIfNotExistsScript();
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = history.GetInsertScript(new HistoryRow(baselineId, ProductInfo.GetVersion()));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<List<SchemaObject>> CreateBaselineSchemaAsync(
        string baselineId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<CastarDbContext>().UseSqlite(connection).Options;
        await using var db = new CastarDbContext(options);
        // Compare against the initial migration so later model changes do not alter what counts as an old database.
        await db.Database.MigrateAsync(baselineId, cancellationToken);
        await using var command = connection.CreateCommand();
        return await ReadSchemaAsync(command, cancellationToken);
    }

    private static async Task<List<SchemaObject>> ReadSchemaAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        command.CommandText = """
            SELECT type, name, sql FROM sqlite_schema
            WHERE sql IS NOT NULL AND name NOT GLOB 'sqlite_*'
                AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
            ORDER BY type, name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schema = new List<SchemaObject>();
        while (await reader.ReadAsync(cancellationToken))
        {
            schema.Add(new SchemaObject(reader.GetString(0), reader.GetString(1),
                Regex.Replace(reader.GetString(2), @"\s+", " ").Trim()));
        }
        return schema;
    }

    private sealed record SchemaObject(string Type, string Name, string Sql);
}
