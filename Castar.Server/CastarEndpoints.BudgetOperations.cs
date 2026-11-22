using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private static async Task<IResult> GetBudgetOperations(DateTime start, DateTime finish,
        DateTimeOffset? startUtc, DateTimeOffset? finishUtc, CastarDbContext db, HttpResponse response,
        CancellationToken token, bool includeOpening = false)
    {
        if (!TryReadRange(start, finish, startUtc, finishUtc, out var first, out var last))
            return InvalidInput("Укажите допустимый период.");

        await db.Database.OpenConnectionAsync(token);
        await using var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(snapshot, token);
        var incomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn <= last)
            .Select(row => new { row.Currency, row.Sum, row.AddedOn, row.RateDate }).ToListAsync(token);
        var purchases = db.Purchases.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn <= last);
        var totals = await GetPurchaseTotalsAsync(db, purchases, token);
        var operations = incomes.Select(row => new BudgetOperationDto(true, row.Currency, row.Sum,
                BudgetTime.AsUtc(row.AddedOn), row.RateDate ?? DateOnly.FromDateTime(BudgetTime.InZone(row.AddedOn, BudgetTime.Belgrade))))
            .Concat(totals.Select(row => row.AsOperation()));
        if (includeOpening)
        {
            operations = operations.Concat(await GetBudgetOpeningOperations(db, first, token));
            response.Headers["X-Castar-Budget-Opening"] = "1";
        }
        return Results.Ok(operations.ToArray());
    }

    private static async Task<IEnumerable<BudgetOperationDto>> GetBudgetOpeningOperations(
        CastarDbContext db, DateTime first, CancellationToken token)
    {
        // Keep equal native amounts together so historical FX still rounds each operation before counting it.
        var incomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn < first && row.RateDate != null)
            .GroupBy(row => new { row.Currency, row.RateDate, row.Sum })
            .Select(group => new BudgetOperationDto(true, group.Key.Currency, group.Key.Sum, null, group.Key.RateDate)
            {
                IsOpening = true,
                OpeningCount = group.LongCount()
            }).ToListAsync(token);
        var purchases = db.Purchases.AsNoTracking().Where(row => row.AddedOn < first);
        var totals = await GetPurchaseTotalsAsync(db, purchases, token);
        var openingPurchases = totals.Where(row => row.RateDate != null || row.ReceiptDate != null)
            .GroupBy(row => new { row.Currency, row.EffectiveRateDate, row.Amount })
            .Select(group => new BudgetOperationDto(false, group.Key.Currency, group.Key.Amount, null, group.Key.EffectiveRateDate)
            {
                IsOpening = true,
                OpeningCount = group.LongCount()
            });

        // Legacy rows need the Belgrade calendar day, including its historical UTC offset.
        var legacyIncomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn < first && row.RateDate == null)
            .Select(row => new { row.Currency, row.Sum, row.AddedOn }).ToListAsync(token);
        var legacy = legacyIncomes.Select(row => new BudgetOperationDto(true, row.Currency, row.Sum, null,
                BudgetTime.BelgradeDate(row.AddedOn)))
            .Concat(totals.Where(row => row.RateDate == null && row.ReceiptDate == null)
                .Select(row => new BudgetOperationDto(false, row.Currency, row.Amount, null, row.EffectiveRateDate)))
            .GroupBy(row => new { row.IsIncome, row.Currency, row.RateDate, row.Amount })
            .Select(group => group.First() with { IsOpening = true, OpeningCount = group.LongCount() });
        return incomes.Concat(openingPurchases).Concat(legacy);
    }

    private static IQueryable<PurchaseBudgetLine> GetPurchaseBudgetLines(CastarDbContext db, IQueryable<Purchase> purchases)
    {
        var items =
            from item in db.Items.AsNoTracking()
            join purchase in purchases on item.PurchaseId equals purchase.Id
            select new PurchaseBudgetLine
            {
                PurchaseId = purchase.Id, CategoryId = item.BudgetCategoryId, Currency = item.Currency,
                Amount = item.Sum, AddedOn = purchase.AddedOn, RateDate = purchase.RateDate, ReceiptDate = purchase.ReceiptDate
            };
        var adjustments =
            from adjustment in db.PurchaseAdjustments.AsNoTracking()
            join purchase in purchases on adjustment.PurchaseId equals purchase.Id
            // A zero adjustment with items can carry receipt metadata; it contributes no expense.
            where adjustment.Amount != 0 || !purchase.Items.Any()
            select new PurchaseBudgetLine
            {
                PurchaseId = purchase.Id, CategoryId = null, Currency = adjustment.Currency,
                Amount = adjustment.Amount, AddedOn = purchase.AddedOn, RateDate = purchase.RateDate, ReceiptDate = purchase.ReceiptDate
            };
        return items.Concat(adjustments);
    }

    private static Task<List<PurchaseBudgetAmount>> GetPurchaseTotalsAsync(CastarDbContext db,
        IQueryable<Purchase> purchases, CancellationToken token) => GetPurchaseBudgetLines(db, purchases)
        .GroupBy(line => new { line.PurchaseId, line.Currency, line.AddedOn, line.RateDate, line.ReceiptDate })
        .Select(group => new PurchaseBudgetAmount(group.Key.Currency, group.Sum(line => line.Amount), group.Key.AddedOn,
            group.Key.RateDate, group.Key.ReceiptDate)).ToListAsync(token);

    private sealed class PurchaseBudgetLine
    {
        public Guid PurchaseId { get; init; }
        public Guid? CategoryId { get; init; }
        public string? Currency { get; init; }
        public decimal Amount { get; init; }
        public DateTime? AddedOn { get; init; }
        public DateOnly? RateDate { get; init; }
        public DateOnly? ReceiptDate { get; init; }
    }

    private sealed record PurchaseBudgetAmount(string? Currency, decimal Amount, DateTime? AddedOn, DateOnly? RateDate, DateOnly? ReceiptDate)
    {
        internal DateOnly? EffectiveRateDate => RateDate ?? ReceiptDate ??
            (AddedOn is { } date ? BudgetTime.BelgradeDate(date) : null);

        internal BudgetOperationDto AsOperation() => new(false, Currency, Amount,
            AddedOn is { } date ? BudgetTime.AsUtc(date) : null, EffectiveRateDate, ReceiptDate);
    }
}
