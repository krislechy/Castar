using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase;

public sealed partial class PurchasesRepository
{
    public Task<int> CountUncategorizedPurchasesAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        ItemsInPeriod(first, endExclusive)
            .Where(item => item.BudgetCategoryId == null && item.CategoryState != ItemCategoryState.Confirmed)
            .Select(item => item.PurchaseId).Distinct().CountAsync(token);

    public Task<int> CountPendingItemsAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        ItemsInPeriod(first, endExclusive).CountAsync(item => item.CategoryState == ItemCategoryState.Pending, token);

    public async Task<(int QueuedCount, int PendingCount)> QueueUncategorizedItemsAsync(CancellationToken cancellationToken,
        DateTime? first = null, DateTime? endExclusive = null)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);
        var queued = await ItemsInPeriod(first, endExclusive).Where(item => item.BudgetCategoryId == null &&
                (item.CategoryState == ItemCategoryState.Archived || item.CategoryState == ItemCategoryState.Automatic))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CategoryState, ItemCategoryState.Pending), cancellationToken);
        var pending = await CountPendingItemsAsync(first, endExclusive, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (queued, pending);
    }

    public async Task<IReadOnlyList<PendingPurchaseClassification>> GetPendingClassificationAsync(
        int limit, CancellationToken cancellationToken, DateTime? first = null, DateTime? endExclusive = null)
    {
        var pending = await ItemsInPeriod(first, endExclusive).AsNoTracking()
            .Where(item => item.CategoryState == ItemCategoryState.Pending)
            .OrderBy(item => item.CreatedOn).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id, item.Name, item.PurchaseId, item.Purchase!.Revision, Merchant = item.Purchase.RetailPlace ?? ""
            }).Take(limit).ToArrayAsync(cancellationToken);
        var rows = pending.Select(item => new { Item = item, Key = CategoryKey(item.Name, item.Merchant) }).ToArray();
        var history = await ReadCategoryHistoryAsync(rows.Select(row => row.Key), cancellationToken);
        return rows.GroupBy(row => new { row.Item.PurchaseId, row.Item.Revision, row.Item.Merchant })
            .Select(group => new PendingPurchaseClassification(group.Key.PurchaseId, group.Key.Revision, group.Key.Merchant,
                group.Select(row =>
                {
                    var found = history.TryGetValue(row.Key, out var categoryId);
                    return new PendingItemClassification(row.Item.Id, row.Item.Name, found, categoryId);
                }).ToArray())).ToArray();
    }

    private IQueryable<Item> ItemsInPeriod(DateTime? first, DateTime? endExclusive) =>
        first is { } from && endExclusive is { } to
            ? Context.Items.Where(item => item.Purchase!.AddedOn >= from && item.Purchase.AddedOn < to)
            : Context.Items;

    public async Task<PurchaseMutationResult> ApplyClassificationAsync(Guid purchaseId, long expectedRevision,
        IReadOnlyDictionary<Guid, Guid?> results, CancellationToken cancellationToken)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);
        var itemIds = results.Keys.ToArray();
        var purchase = await Context.Purchases.Include(row => row.Items.Where(item => itemIds.Contains(item.Id)))
            .SingleOrDefaultAsync(row => row.Id == purchaseId, cancellationToken);
        if (purchase is null)
            return PurchaseMutationResult.NotFound;
        if (purchase.Revision != expectedRevision)
            return PurchaseMutationResult.Conflict;
        if (purchase.Items.Count != results.Count)
            return PurchaseMutationResult.NotFound;

        var pending = purchase.Items.Where(item => item.CategoryState == ItemCategoryState.Pending)
            .Select(item => new { Item = item, Key = CategoryKey(item.Name, purchase.RetailPlace) }).ToArray();
        if (pending.Length == 0)
            return PurchaseMutationResult.Unchanged;

        var history = await ReadCategoryHistoryAsync(pending.Select(row => row.Key), cancellationToken);
        foreach (var row in pending)
        {
            row.Item.BudgetCategoryId = history.TryGetValue(row.Key, out var categoryId) ? categoryId : results[row.Item.Id];
            row.Item.CategoryState = ItemCategoryState.Automatic;
        }
        purchase.Revision = checked(purchase.Revision + 1);
        await Context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PurchaseMutationResult.Applied;
    }

    internal static string NormalizeCategoryKeyPart(string? text) => string.IsNullOrWhiteSpace(text)
        ? ""
        : string.Join(' ', text.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    internal static void ResetAutomaticCategory(Item item)
    {
        if (item.CategoryState == ItemCategoryState.Automatic)
        {
            item.CategoryState = ItemCategoryState.Pending;
            item.BudgetCategoryId = null;
        }
    }

    private async Task<Dictionary<string, Guid?>> ReadCategoryHistoryAsync(IEnumerable<string> keys, CancellationToken token)
    {
        var values = keys.Distinct().ToArray();
        return values.Length == 0 ? [] : await Context.ItemCategoryHistory.AsNoTracking()
            .Where(entry => values.Contains(entry.Key)).ToDictionaryAsync(entry => entry.Key, entry => entry.BudgetCategoryId, token);
    }

    private async Task ConfirmCategoryAsync(Purchase purchase, Item item, Guid? categoryId, CancellationToken token)
    {
        item.BudgetCategoryId = categoryId;
        item.CategoryState = ItemCategoryState.Confirmed;
        var key = CategoryKey(item.Name, purchase.RetailPlace);
        await Context.Database.ExecuteSqlAsync($"""
            INSERT INTO "ItemCategoryHistory" ("Key", "BudgetCategoryId") VALUES ({key}, {categoryId})
            ON CONFLICT ("Key") DO UPDATE SET "BudgetCategoryId" = excluded."BudgetCategoryId"
            """, token);
    }

    private static string CategoryKey(string name, string? merchant)
    {
        // JSON strings form an unambiguous pair even if a product name contains a separator character.
        var pair = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new[] { NormalizeCategoryKeyPart(name), NormalizeCategoryKeyPart(merchant) });
        return Convert.ToHexString(SHA256.HashData(pair));
    }
}
