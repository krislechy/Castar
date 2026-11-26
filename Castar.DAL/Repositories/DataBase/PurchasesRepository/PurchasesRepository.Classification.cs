using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase;

public sealed partial class PurchasesRepository
{
    public Task<int> CountUncategorizedPurchasesAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        ItemsInPeriod(first, endExclusive)
            .Where(item => item.BudgetCategoryId == null)
            .Select(item => item.PurchaseId).Distinct().CountAsync(token);

    public Task<int> CountPendingItemsAsync(DateTime? first, DateTime? endExclusive, CancellationToken token) =>
        PendingClassificationItems(first, endExclusive).CountAsync(token);

    public async Task<(int QueuedCount, int PendingCount)> QueueUncategorizedItemsAsync(CancellationToken cancellationToken,
        DateTime? first = null, DateTime? endExclusive = null)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);
        var queued = await ItemsInPeriod(first, endExclusive).Where(item => item.BudgetCategoryId == null &&
                item.CategoryState != ItemCategoryState.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CategoryState, ItemCategoryState.Pending), cancellationToken);
        var pending = await CountPendingItemsAsync(first, endExclusive, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (queued, pending);
    }

    public async Task<IReadOnlyList<PendingPurchaseClassification>> GetPendingClassificationAsync(
        int limit, CancellationToken cancellationToken, DateTime? first = null, DateTime? endExclusive = null)
    {
        var pending = await PendingClassificationItems(first, endExclusive).AsNoTracking()
            .OrderBy(item => item.CreatedOn).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id, item.Name, item.PurchaseId, item.Purchase!.Revision,
                Merchant = item.Purchase.RetailPlace ?? "", PurchaseName = item.Purchase.Name
            }).Take(limit).ToArrayAsync(cancellationToken);
        var rows = pending.Select(item => new { Item = item, Key = CategoryKey(item.Name, item.Merchant) }).ToArray();
        var history = await ReadCategoryHistoryAsync(rows.Select(row => row.Key), cancellationToken);
        return rows.GroupBy(row => new { row.Item.PurchaseId, row.Item.Revision, row.Item.Merchant, row.Item.PurchaseName })
            .Select(group => new PendingPurchaseClassification(group.Key.PurchaseId, group.Key.Revision, group.Key.Merchant,
                group.Select(row =>
                {
                    var found = history.TryGetValue(row.Key, out var categoryId);
                    return new PendingItemClassification(row.Item.Id, row.Item.Name, found, categoryId);
                }).ToArray(), group.Key.PurchaseName)).ToArray();
    }

    private IQueryable<Item> ItemsInPeriod(DateTime? first, DateTime? endExclusive) =>
        first is { } from && endExclusive is { } to
            ? Context.Items.Where(item => item.Purchase!.AddedOn >= from && item.Purchase.AddedOn < to)
            : Context.Items;

    private IQueryable<Item> PendingClassificationItems(DateTime? first, DateTime? endExclusive) =>
        ItemsInPeriod(first, endExclusive).Where(item => item.CategoryState == ItemCategoryState.Pending ||
            item.CategoryState == ItemCategoryState.Confirmed && item.BudgetCategoryId == null);

    public async Task<PurchaseMutationResult> ApplyClassificationAsync(Guid purchaseId, long expectedRevision,
        IReadOnlyDictionary<Guid, Guid?> results, CancellationToken cancellationToken, bool reclassify = false)
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
        if (reclassify && purchase.Items.Any(item => item.CategoryState is not
            (ItemCategoryState.Pending or ItemCategoryState.Automatic or ItemCategoryState.Archived or ItemCategoryState.Confirmed)))
            return PurchaseMutationResult.Conflict;

        var items = purchase.Items.Where(item => reclassify || item.CategoryState == ItemCategoryState.Pending ||
                item.CategoryState == ItemCategoryState.Confirmed && item.BudgetCategoryId == null)
            .Select(item => new { Item = item, Key = reclassify ? null : CategoryKey(item.Name, purchase.RetailPlace) })
            .ToArray();
        if (items.Length == 0)
            return PurchaseMutationResult.Unchanged;

        var history = reclassify ? null : await ReadCategoryHistoryAsync(items.Select(row => row.Key!), cancellationToken);
        var changed = false;
        foreach (var row in items)
        {
            var categoryId = history is not null && history.TryGetValue(row.Key!, out var remembered)
                ? remembered : results[row.Item.Id];
            if (row.Item.BudgetCategoryId == categoryId &&
                (row.Item.CategoryState == ItemCategoryState.Automatic ||
                    row.Item.CategoryState == ItemCategoryState.Confirmed && categoryId.HasValue))
                continue;

            row.Item.BudgetCategoryId = categoryId;
            row.Item.CategoryState = ItemCategoryState.Automatic;
            changed = true;
        }
        if (!changed)
            return PurchaseMutationResult.Unchanged;

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
            .Where(entry => entry.BudgetCategoryId != null && values.Contains(entry.Key))
            .ToDictionaryAsync(entry => entry.Key, entry => entry.BudgetCategoryId, token);
    }

    private async Task ConfirmCategoryAsync(Purchase purchase, Item item, Guid? categoryId, CancellationToken token)
    {
        item.BudgetCategoryId = categoryId;
        var key = CategoryKey(item.Name, purchase.RetailPlace);
        if (!categoryId.HasValue)
        {
            item.CategoryState = ItemCategoryState.Pending;
            await Context.ItemCategoryHistory.Where(entry => entry.Key == key).ExecuteDeleteAsync(token);
            return;
        }

        item.CategoryState = ItemCategoryState.Confirmed;
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
