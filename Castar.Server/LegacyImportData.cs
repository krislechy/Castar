using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal sealed class LegacyImportData
{
    private readonly Income[] incomes;
    private readonly Purchase[] purchases;
    private readonly Dictionary<Guid, long> ownerIds;

    private LegacyImportData(Income[] incomes, Purchase[] purchases, Dictionary<Guid, long> ownerIds, ArchiveSummary summary)
    {
        this.incomes = incomes;
        this.purchases = purchases;
        this.ownerIds = ownerIds;
        Summary = summary;
        Dates = incomes.Select(entry => entry.RateDate!.Value)
            .Concat(purchases.Select(entry => entry.RateDate!.Value)).Distinct().Order().ToArray();
    }

    public DateOnly[] Dates { get; }
    public ArchiveSummary Summary { get; }

    public static LegacyImportData Prepare(Archive archive)
    {
        if (archive.Format != 1 || archive.ArchiveSha256 is not { Length: 64 } || !archive.ArchiveSha256.All(Uri.IsHexDigit)
            || archive.HistoryTransactionCount < 0)
            throw new InvalidDataException("Unexpected archive export version or fingerprint.");
        ValidateIds(archive.Incomes, "income", row => row.Id);
        ValidateIds(archive.Purchases, "purchase", row => row.Id);
        ValidateIds(archive.Items, "item", row => row.Id);

        var purchaseIds = archive.Purchases.Select(purchase => purchase.Id).ToHashSet();
        foreach (var item in archive.Items)
        {
            if (!purchaseIds.Contains(item.PurchaseId) || !double.IsFinite(item.Quantity) || item.Quantity <= 0)
                throw new InvalidDataException($"Invalid purchase or quantity for archive item {item.Id}.");
        }

        var telegramIds = archive.Purchases.Select(row => row.TelegramId).Concat(archive.Incomes.Select(row => row.TelegramId))
            .Distinct().ToArray();
        if (telegramIds.Any(id => id <= 0))
            throw new InvalidDataException("An archived operation does not have a valid Telegram owner.");

        var items = archive.Items.OrderBy(row => row.Id).ToLookup(row => row.PurchaseId);
        var moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        var ambiguous = new List<string>();
        var incomes = archive.Incomes.Select(row => new Income
        {
            Id = StableId("income", row.Id),
            UserId = StableId("owner", row.TelegramId),
            Source = Text(row.Source, "Доход", "income", row.Id),
            Sum = row.Amount,
            Currency = LegacyImport.Currency,
            AddedOn = ToUtc(row.AddedOn, "income", row.Id),
            RateDate = DateOnly.FromDateTime(row.AddedOn)
        }).ToArray();
        var purchases = archive.Purchases.Select(row => new Purchase
        {
            Id = StableId("purchase", row.Id),
            UserId = StableId("owner", row.TelegramId),
            Name = Text(row.Name, "Покупка", "purchase", row.Id),
            TotalSum = row.Amount,
            Currency = LegacyImport.Currency,
            AddedOn = ToUtc(row.AddedOn, "purchase", row.Id),
            RateDate = DateOnly.FromDateTime(row.AddedOn),
            Items = new(items[row.Id].Select(item => new Item
            {
                Id = StableId("item", item.Id),
                PurchaseId = StableId("purchase", row.Id),
                Name = Text(item.Name, "Товар", "item", item.Id),
                Quantity = item.Quantity,
                Price = item.Price,
                Sum = item.Amount,
                Category = item.Category
            }))
        }).ToArray();
        var summary = new ArchiveSummary(archive.ArchiveSha256, telegramIds.Length,
            incomes.Length, purchases.Length, archive.Items.Length, incomes.Sum(row => row.Sum), purchases.Sum(row => row.TotalSum),
            archive.Items.Sum(row => row.Amount), archive.HistoryTransactionCount,
            archive.Incomes.Count(row => string.IsNullOrWhiteSpace(row.Source)),
            archive.Purchases.Count(row => string.IsNullOrWhiteSpace(row.Name)),
            archive.Items.Count(row => string.IsNullOrWhiteSpace(row.Name)),
            purchases.Count(row => row.TotalSum != row.Items.Sum(item => item.Sum)),
            archive.Items.Count(row => Math.Abs((double)row.Amount - (double)row.Price * row.Quantity) > 0.005001),
            ambiguous.ToArray());
        return new(incomes, purchases, telegramIds.ToDictionary(id => StableId("owner", id)), summary);

        DateTime ToUtc(DateTime value, string kind, int id)
        {
            if (value.Kind != DateTimeKind.Unspecified || value.Year < 1900 || moscow.IsInvalidTime(value))
                throw new InvalidDataException($"Invalid Moscow local time for archive {kind} {id}.");
            if (!moscow.IsAmbiguousTime(value))
                return TimeZoneInfo.ConvertTimeToUtc(value, moscow);

            ambiguous.Add(FormattableString.Invariant($"{kind} {id}: {value:yyyy-MM-dd HH:mm:ss}"));
            return new DateTimeOffset(value, moscow.GetAmbiguousTimeOffsets(value).Max()).UtcDateTime;
        }
    }

    public async Task<ImportResult> ImportAsync(
        CastarDbContext db, string? target, CurrencyRateDto[] quotes, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var knownOwners = await db.Users.AsNoTracking().ToListAsync(token);
        var owners = new Dictionary<Guid, Guid>();
        var createdOwners = 0;
        foreach (var source in ownerIds)
        {
            var matches = knownOwners.Where(user => user.TelegramId == source.Value).ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException("More than one user has the Telegram ID of an archived owner.");
            if (matches.Length == 1)
            {
                owners[source.Key] = matches[0].Id;
                continue;
            }
            if (knownOwners.Any(user => user.Id == source.Key))
                throw new InvalidDataException("An archived owner ID conflicts with an existing user.");

            db.Users.Add(new User
            {
                Id = source.Key, TelegramId = source.Value, Name = "Пользователь из архива",
                ColorRaw = [255, 85, 94, 108], TelegramNotificationsEnabled = false
            });
            owners[source.Key] = source.Key;
            createdOwners++;
        }

        var incomeCount = 0;
        foreach (var batch in incomes.Chunk(200))
        {
            var ids = batch.Select(row => row.Id).ToArray();
            var current = await db.Incomes.AsNoTracking().Where(row => ids.Contains(row.Id)).ToDictionaryAsync(row => row.Id, token);
            foreach (var source in batch)
            {
                var ownerId = owners[source.UserId];
                if (current.TryGetValue(source.Id, out var entry))
                {
                    if (entry.UserId != ownerId || entry.Sum != source.Sum || entry.Source != source.Source ||
                        entry.AddedOn != source.AddedOn || entry.Currency != source.Currency || entry.RateDate != source.RateDate)
                        throw new InvalidDataException($"Archive income {source.Id} conflicts with an existing record.");
                    continue;
                }
                var created = source.Clone<Income>();
                created.UserId = ownerId;
                db.Incomes.Add(created);
                incomeCount++;
            }
        }

        var purchaseCount = 0;
        var itemCount = 0;
        foreach (var batch in purchases.Chunk(200))
        {
            var ids = batch.Select(row => row.Id).ToArray();
            var current = await db.Purchases.AsNoTracking().Include(row => row.Items)
                .Where(row => ids.Contains(row.Id)).ToDictionaryAsync(row => row.Id, token);
            foreach (var source in batch)
            {
                var ownerId = owners[source.UserId];
                if (current.TryGetValue(source.Id, out var entry))
                {
                    if (!SamePurchase(source, entry, ownerId))
                        throw new InvalidDataException($"Archive purchase {source.Id} conflicts with an existing record.");
                    continue;
                }
                var created = source.Clone<Purchase>();
                created.UserId = ownerId;
                created.Items = new(source.Items.Select(item => item.Clone<Item>()));
                db.Purchases.Add(created);
                var line = 0;
                foreach (var item in created.Items)
                    db.Entry(item).Property<int?>("LineNumber").CurrentValue = line++;
                purchaseCount++;
                itemCount += created.Items.Count;
            }
        }

        var rateCount = await WriteMissingQuotesAsync(db, target, quotes, token);
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(createdOwners, incomeCount, purchaseCount, itemCount, rateCount);
    }

    private static bool SamePurchase(Purchase source, Purchase entry, Guid ownerId) =>
        entry.UserId == ownerId && entry.Name == source.Name && entry.TotalSum == source.TotalSum &&
        entry.Currency == source.Currency && entry.AddedOn == source.AddedOn && entry.RateDate == source.RateDate &&
        entry.ReceiptDate is null && entry.RawData is null && entry.ReceiptSource is null &&
        entry.RetailPlace is null && entry.RetailPlaceAddress is null &&
        entry.Items.OrderBy(item => item.Id).Select(ItemKey).SequenceEqual(source.Items.OrderBy(item => item.Id).Select(ItemKey));

    private static (Guid Id, string Name, double Quantity, decimal Price, decimal Amount, int? Category) ItemKey(Item item) =>
        (item.Id, item.Name, item.Quantity, item.Price, item.Sum, item.Category);

    private static async Task<int> WriteMissingQuotesAsync(
        CastarDbContext db, string? target, CurrencyRateDto[] quotes, CancellationToken token)
    {
        if (target is null or LegacyImport.Currency || quotes.Length == 0)
            return 0;

        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var batch in quotes.Chunk(200))
        {
            var dates = batch.Select(quote => quote.Date).ToArray();
            var stored = await db.ExchangeRates.Where(row => row.Target == target && row.Source == LegacyImport.Currency && dates.Contains(row.Date))
                .ToDictionaryAsync(row => row.Date, token);
            foreach (var quote in batch)
            {
                if (stored.TryGetValue(quote.Date, out var existing))
                {
                    if (existing.Rate > 0 && existing.QuotedOn >= quote.QuotedOn && existing.QuotedOn <= quote.Date)
                        continue;
                    existing.QuotedOn = quote.QuotedOn;
                    existing.Rate = quote.Rate;
                    existing.RetrievedUtc = now;
                }
                else
                {
                    db.ExchangeRates.Add(new ExchangeRateEntry
                    {
                        Source = LegacyImport.Currency, Target = target, Date = quote.Date,
                        QuotedOn = quote.QuotedOn, Rate = quote.Rate, RetrievedUtc = now
                    });
                }
                count++;
            }
        }
        return count;
    }

    private static string Text(string? value, string fallback, string kind, int id)
    {
        if (value?.Length > 400)
            throw new InvalidDataException($"Archive {kind} {id} has a name longer than 400 characters.");
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static Guid StableId(string kind, long sourceId)
    {
        var name = string.Format(CultureInfo.InvariantCulture, "Castar:February2022Archive:v1:{0}:{1}", kind, sourceId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        hash[6] = (byte)((hash[6] & 0xf) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static void ValidateIds<T>(T[]? rows, string kind, Func<T, int> id) where T : class
    {
        if (rows is null || rows.Any(row => row is null || id(row) <= 0) ||
            rows.Select(id).Distinct().Count() != rows.Length)
            throw new InvalidDataException($"Missing or duplicate archive {kind} identifiers.");
    }

    internal sealed record Archive(int Format, string ArchiveSha256, int HistoryTransactionCount,
        ArchiveIncome[] Incomes, ArchivePurchase[] Purchases, ArchiveItem[] Items);
    internal sealed record ArchiveIncome(int Id, long TelegramId, DateTime AddedOn, decimal Amount, string? Source);
    internal sealed record ArchivePurchase(int Id, long TelegramId, DateTime AddedOn, decimal Amount, string? Name);
    internal sealed record ArchiveItem(int Id, int PurchaseId, string? Name, double Quantity, decimal Price, decimal Amount, int? Category);
    internal sealed record ArchiveSummary(string Sha256, int Owners, int Incomes, int Purchases, int Items,
        decimal IncomeTotalRub, decimal PurchaseTotalRub, decimal ItemTotalRub, int HistoryRowsOutsideBudgetModel,
        int MissingIncomeNames, int MissingPurchaseNames, int MissingItemNames, int PurchasesWithDifferentItemTotals,
        int ItemsWithDifferentCalculatedAmounts, string[] AmbiguousMoscowTimes);
    internal sealed record ImportResult(int UsersAdded, int IncomesAdded, int PurchasesAdded, int ItemsAdded, int RatesWritten);
}
