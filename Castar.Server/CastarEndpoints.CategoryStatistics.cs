using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private static async Task<IResult> GetBudgetCategoryBreakdown(DateTimeOffset startUtc,
        DateTimeOffset endExclusiveUtc, CastarDbContext db, CancellationToken token)
    {
        var first = startUtc.UtcDateTime;
        var endExclusive = endExclusiveUtc.UtcDateTime;
        if (first >= endExclusive)
            return InvalidInput("Укажите допустимый период.");

        await db.Database.OpenConnectionAsync(token);
        await using var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(snapshot, token);

        // Keep equal native amounts together so the client can round each converted operation before multiplying.
        var counted = new Dictionary<(bool IsIncome, Guid? CategoryId, string? Currency, decimal Amount, DateOnly RateDate), long>();
        void Add(bool isIncome, Guid? categoryId, string? currency, decimal amount, DateOnly rateDate, long count = 1)
        {
            var key = (isIncome, categoryId, currency, amount, rateDate);
            counted[key] = counted.GetValueOrDefault(key) + count;
        }

        var incomes = db.Incomes.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn < endExclusive)
            .GroupBy(row => new
            {
                row.CategoryId, row.Currency, row.Sum, row.RateDate,
                LegacyAddedOn = row.RateDate == null ? (DateTime?)row.AddedOn : null
            })
            .Select(group => new
            {
                group.Key.CategoryId, group.Key.Currency, group.Key.Sum, group.Key.RateDate, group.Key.LegacyAddedOn,
                Count = group.LongCount()
            });
        await foreach (var income in incomes.AsAsyncEnumerable().WithCancellation(token))
            Add(true, income.CategoryId, income.Currency, income.Sum,
                income.RateDate ?? BudgetTime.BelgradeDate(income.LegacyAddedOn!.Value), income.Count);

        var purchases = db.Purchases.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn < endExclusive);
        var totals = GetPurchaseBudgetLines(db, purchases)
            .GroupBy(line => new { line.PurchaseId, line.CategoryId, line.Currency, line.AddedOn, line.RateDate, line.ReceiptDate })
            .Select(group => new
            {
                group.Key.CategoryId, group.Key.Currency, group.Key.AddedOn, group.Key.RateDate, group.Key.ReceiptDate,
                Amount = group.Sum(line => line.Amount)
            });
        await foreach (var total in totals.AsAsyncEnumerable().WithCancellation(token))
            Add(false, total.CategoryId, total.Currency, total.Amount,
                total.RateDate ?? total.ReceiptDate ?? BudgetTime.BelgradeDate(total.AddedOn!.Value));

        return Results.Ok(counted.Select(row => new BudgetCategoryAmountDto(row.Key.IsIncome, row.Key.CategoryId,
            row.Key.Currency, row.Key.Amount, row.Key.RateDate, row.Value)).ToArray());
    }
}
