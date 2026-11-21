using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase;

public sealed partial class IncomesRepository
{
    public Task<int> CountUncategorizedAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        IncomesInPeriod(first, endExclusive).CountAsync(
            income => income.CategoryId == null && income.CategoryState != IncomeCategoryState.Confirmed, token);

    public Task<int> CountPendingClassificationAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        IncomesInPeriod(first, endExclusive).CountAsync(income => income.CategoryState == IncomeCategoryState.Pending, token);

    public async Task<(int QueuedCount, int PendingCount)> QueueUncategorizedAsync(DateTime? first,
        DateTime? endExclusive, CancellationToken token)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync(token);
        var queued = await IncomesInPeriod(first, endExclusive)
            .Where(income => income.CategoryId == null &&
                (income.CategoryState == IncomeCategoryState.Archived || income.CategoryState == IncomeCategoryState.Automatic))
            .ExecuteUpdateAsync(update => update.SetProperty(income => income.CategoryState, IncomeCategoryState.Pending), token);
        var pending = await CountPendingClassificationAsync(first, endExclusive, token);
        await transaction.CommitAsync(token);
        return (queued, pending);
    }

    public async Task<IReadOnlyList<PendingIncomeClassification>> GetPendingClassificationAsync(int limit,
        DateTime? first, DateTime? endExclusive, CancellationToken token)
    {
        var pending = await IncomesInPeriod(first, endExclusive).AsNoTracking()
            .Where(income => income.CategoryState == IncomeCategoryState.Pending)
            .OrderBy(income => income.CreatedOn).ThenBy(income => income.Id)
            .Select(income => new { income.Id, income.CategoryRevision, income.Source })
            .Take(limit).ToArrayAsync(token);
        var keyed = pending.Select(income => new { Income = income, Key = CategoryKey(income.Source) }).ToArray();
        var keys = keyed.Select(entry => entry.Key).Distinct().ToArray();
        var history = keys.Length == 0 ? new Dictionary<string, Guid?>() : await Context.IncomeCategoryHistory.AsNoTracking()
            .Where(entry => keys.Contains(entry.Key)).ToDictionaryAsync(entry => entry.Key, entry => entry.CategoryId, token);
        return keyed.Select(entry =>
        {
            var found = history.TryGetValue(entry.Key, out var category);
            return new PendingIncomeClassification(entry.Income.Id, entry.Income.CategoryRevision,
                entry.Income.Source, found, category);
        }).ToArray();
    }

    public async Task<IncomeClassificationResult> ApplyClassificationAsync(Guid id, long revision, Guid? categoryId,
        CancellationToken token)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync(token);
        var income = await Context.Incomes.SingleOrDefaultAsync(income => income.Id == id, token);
        if (income is null)
            return IncomeClassificationResult.NotFound;
        if (income.CategoryRevision != revision)
            return IncomeClassificationResult.Conflict;
        if (income.CategoryState != IncomeCategoryState.Pending)
            return IncomeClassificationResult.Unchanged;

        var key = CategoryKey(income.Source);
        var history = await Context.IncomeCategoryHistory.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Key == key, token);
        income.CategoryId = history is null ? categoryId : history.CategoryId;
        income.CategoryState = IncomeCategoryState.Automatic;
        income.CategoryRevision = checked(income.CategoryRevision + 1);
        await Context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return IncomeClassificationResult.Applied;
    }

    private IQueryable<Income> IncomesInPeriod(DateTime? first, DateTime? endExclusive) =>
        first is { } from && endExclusive is { } to
            ? Context.Incomes.Where(income => income.AddedOn >= from && income.AddedOn < to)
            : Context.Incomes;

    private async Task ConfirmCategoryAsync(Income income, Guid? categoryId, CancellationToken token)
    {
        income.CategoryId = categoryId;
        income.CategoryState = IncomeCategoryState.Confirmed;
        var key = CategoryKey(income.Source);
        await Context.Database.ExecuteSqlAsync($"""
            INSERT INTO "IncomeCategoryHistory" ("Key", "CategoryId") VALUES ({key}, {categoryId})
            ON CONFLICT ("Key") DO UPDATE SET "CategoryId" = excluded."CategoryId"
            """, token);
    }

    private static bool UpdateSource(Income income, string source)
    {
        var changed = PurchasesRepository.NormalizeCategoryKeyPart(source) !=
            PurchasesRepository.NormalizeCategoryKeyPart(income.Source);
        income.Source = source;
        if (changed && income.CategoryState == IncomeCategoryState.Automatic)
        {
            income.CategoryId = null;
            income.CategoryState = IncomeCategoryState.Pending;
        }
        return changed;
    }

    private static string CategoryKey(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PurchasesRepository.NormalizeCategoryKeyPart(source))));
}
