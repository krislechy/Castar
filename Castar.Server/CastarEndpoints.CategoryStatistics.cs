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
        void Add(bool isIncome, Guid? categoryId, string? currency, decimal amount, DateOnly rateDate)
        {
            var key = (isIncome, categoryId, currency, amount, rateDate);
            counted[key] = counted.GetValueOrDefault(key) + 1;
        }

        var incomes = db.Incomes.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn < endExclusive)
            .Select(row => new { row.CategoryId, row.Currency, row.Sum, row.AddedOn, row.RateDate });
        await foreach (var income in incomes.AsAsyncEnumerable().WithCancellation(token))
            Add(true, income.CategoryId, income.Currency, income.Sum, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn));

        var purchases = db.Purchases.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn < endExclusive);
        var totals = await (
            from item in db.Items.AsNoTracking()
            join purchase in purchases on item.PurchaseId equals purchase.Id
            group item by new { item.PurchaseId, item.BudgetCategoryId } into category
            select new { category.Key.PurchaseId, category.Key.BudgetCategoryId, Amount = category.Sum(item => item.Sum) }
        ).ToArrayAsync(token);
        var byPurchase = totals.ToLookup(row => row.PurchaseId);

        await foreach (var purchase in purchases.Select(row => new
            {
                row.Id, row.Currency, row.TotalSum, row.AddedOn, row.RateDate, row.ReceiptDate
            }).AsAsyncEnumerable().WithCancellation(token))
        {
            var date = purchase.RateDate ?? purchase.ReceiptDate ?? BudgetTime.BelgradeDate(purchase.AddedOn!.Value);
            var uncategorized = purchase.TotalSum;
            var categories = byPurchase[purchase.Id];
            var hasUncategorizedItems = false;
            foreach (var category in categories)
            {
                if (category.BudgetCategoryId is not { } categoryId)
                {
                    hasUncategorizedItems = true;
                    continue;
                }
                Add(false, categoryId, purchase.Currency, category.Amount, date);
                uncategorized -= category.Amount;
            }
            // Keep unlabelled items and any receipt adjustment in one share; the header is the amount actually paid.
            if (uncategorized != 0 || hasUncategorizedItems || !categories.Any())
                Add(false, null, purchase.Currency, uncategorized, date);
        }

        return Results.Ok(counted.Select(row => new BudgetCategoryAmountDto(row.Key.IsIncome, row.Key.CategoryId,
            row.Key.Currency, row.Key.Amount, row.Key.RateDate, row.Value)).ToArray());
    }
}
