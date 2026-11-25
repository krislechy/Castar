using System.Text.Json;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal static class LegacyImport
{
    internal const string Currency = "RUB";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!TryParse(arguments, out var options))
        {
            Console.Error.WriteLine("Usage: Castar.Server --import-legacy <export.json> <castar.sqlite> [--target RSD] [--replace-before-2026] [--apply]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        if (!OperatingSystem.IsIOS())
            Console.CancelKeyPress += Cancel;
        DirectoryInfo? temporary = null;
        try
        {
            var archive = await ReadArchiveAsync(options.ExportPath, cancellation.Token);
            var data = LegacyImportData.Prepare(archive);
            var replaceBefore = options.ReplaceBefore2026 ? LegacyImportData.StartOf2026Utc : (DateTime?)null;
            data.ValidateReplacement(replaceBefore);
            var source = new FileInfo(options.DatabasePath);
            if (!source.Exists || source.Length == 0)
                throw new InvalidDataException("The target must be an existing Castar SQLite database.");

            temporary = Directory.CreateTempSubdirectory("castar-legacy-import-");
            var copyPath = Path.Combine(temporary.FullName, "castar.sqlite");
            await SnapshotAsync(source.FullName, copyPath, cancellation.Token);
            await using var copy = OpenDatabase(copyPath);
            await CastarSqliteSchema.MigrateAsync(copy, cancellation.Token);
            CurrencyRateDto[] quotes = [];
            if (options.Target is not null)
            {
                var quotesPath = Path.Combine(temporary.FullName, "quotes.sqlite");
                await SnapshotAsync(copyPath, quotesPath, cancellation.Token);
                quotes = await LoadQuotesAsync(quotesPath, options.Target, data.Dates, cancellation.Token);
            }
            var preview = await data.ImportAsync(copy, options.Target, quotes, replaceBefore, cancellation.Token);
            await CheckIntegrityAsync(copy, cancellation.Token);

            string? backup = null;
            var result = preview;
            if (options.Apply)
            {
                backup = Path.Combine(source.DirectoryName!,
                    $"castar-before-legacy-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
                await SnapshotAsync(source.FullName, backup, cancellation.Token);
                Console.Error.WriteLine($"Pre-import backup: {backup}");
                await using var database = OpenDatabase(source.FullName);
                await CastarSqliteSchema.MigrateAsync(database, cancellation.Token);
                result = await data.ImportAsync(database, options.Target, quotes, replaceBefore, cancellation.Token);
                await CheckIntegrityAsync(database, cancellation.Token);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = options.Apply ? "applied" : "preview",
                Backup = backup,
                ReplaceBeforeUtc = replaceBefore,
                Archive = data.Summary,
                Result = result,
                Rates = new
                {
                    Target = options.Target,
                    Exact = quotes.Count(quote => quote.Date == quote.QuotedOn),
                    Previous = quotes.Count(quote => quote.Date != quote.QuotedOn),
                    Missing = options.Target is null ? [] : data.Dates.Except(quotes.Select(quote => quote.Date)).ToArray()
                }
            }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Import canceled.");
            return 130;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("The archive export JSON does not have the expected format.");
            return 1;
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or
                                      DbUpdateException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Legacy import failed ({error.GetType().Name}).");
            return 1;
        }
        finally
        {
            if (!OperatingSystem.IsIOS())
                Console.CancelKeyPress -= Cancel;
            if (temporary is not null)
                RemoveTemporaryDatabase(temporary);
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static async Task<LegacyImportData.Archive> ReadArchiveAsync(string path, CancellationToken token)
    {
        const long maxExportBytes = 64 * 1024 * 1024;
        await using var input = File.OpenRead(path);
        if (input.Length == 0 || input.Length > maxExportBytes)
            throw new InvalidDataException("The archive export size is outside the supported range.");
        return await JsonSerializer.DeserializeAsync<LegacyImportData.Archive>(input, Json, token)
            ?? throw new InvalidDataException("The archive export is empty.");
    }

    private static async Task<CurrencyRateDto[]> LoadQuotesAsync(
        string path, string? target, DateOnly[] dates, CancellationToken token)
    {
        if (target is null)
            return [];

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CastarDbContext>(options => options.UseSqlite(ConnectionString(path, SqliteOpenMode.ReadWrite)));
        services.AddExchangeRates();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ExchangeRateCache>();
        var quotes = new List<CurrencyRateDto>(dates.Length);
        foreach (var batch in dates.Chunk(256))
            quotes.AddRange(await cache.GetAsync(target, batch.Select(date => new CurrencyRateKeyDto(Currency, date)).ToArray(), token));
        return quotes.ToArray();
    }

    private static async Task SnapshotAsync(string sourcePath, string destinationPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using (PrivateServerFile.Create(destinationPath))
        { }
        try
        {
            await using var source = new SqliteConnection(ConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
            await using var destination = new SqliteConnection(ConnectionString(destinationPath, SqliteOpenMode.ReadWrite));
            await source.OpenAsync(token);
            await destination.OpenAsync(token);
            source.BackupDatabase(destination);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static async Task CheckIntegrityAsync(CastarDbContext database, CancellationToken token)
    {
        await database.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            if (!Equals(await command.ExecuteScalarAsync(token), "ok"))
                throw new InvalidDataException("SQLite integrity_check did not pass.");
            command.CommandText = "PRAGMA foreign_key_check;";
            if (await command.ExecuteScalarAsync(token) is not null)
                throw new InvalidDataException("SQLite foreign_key_check did not pass.");
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }

    private static CastarDbContext OpenDatabase(string path) => new(
        new DbContextOptionsBuilder<CastarDbContext>()
            .UseSqlite(ConnectionString(path, SqliteOpenMode.ReadWrite)).Options);

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        ForeignKeys = true,
        Pooling = false,
        DefaultTimeout = 15
    }.ConnectionString;

    private static void RemoveTemporaryDatabase(DirectoryInfo directory)
    {
        try
        {
            foreach (var name in new[] { "castar.sqlite", "quotes.sqlite" })
            foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
                File.Delete(Path.Combine(directory.FullName, name + suffix));
            directory.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The temporary database could not be removed.");
        }
    }

    private static bool TryParse(string[] arguments, out Options options)
    {
        options = null!;
        if (arguments.Length < 2 || string.IsNullOrWhiteSpace(arguments[0]) || string.IsNullOrWhiteSpace(arguments[1])
            || arguments[0].StartsWith("--", StringComparison.Ordinal) || arguments[1].StartsWith("--", StringComparison.Ordinal))
            return false;
        string? target = null;
        var apply = false;
        var replaceBefore2026 = false;
        for (var index = 2; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--target" when target is null && index + 1 < arguments.Length:
                    target = ManualCurrencySettings.NormalizeCode(arguments[++index]);
                    if (target is null)
                        return false;
                    break;
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--replace-before-2026" when !replaceBefore2026:
                    replaceBefore2026 = true;
                    break;
                default:
                    return false;
            }
        }
        options = new(arguments[0], arguments[1], target, apply, replaceBefore2026);
        return true;
    }

    private sealed record Options(string ExportPath, string DatabasePath, string? Target, bool Apply, bool ReplaceBefore2026);
}
