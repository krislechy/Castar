using System.Globalization;
using Castar.Api;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private const int ClassificationBatchLimit = 250;
    private const int PurchaseReclassificationLimit = 5_000;

    private static async Task<IResult> CountUncategorizedPurchases(IPurchasesRepository purchases,
        CancellationToken token, DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");
        return Results.Ok(new ClassificationCountDto(
            await purchases.CountUncategorizedPurchasesAsync(first, endExclusive, token)));
    }

    private static async Task<IResult> QueueUncategorizedPurchaseItems(IPurchasesRepository purchases,
        BudgetEventPublisher events, CancellationToken token,
        DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");
        var (queued, pending) = await purchases.QueueUncategorizedItemsAsync(token, first, endExclusive);
        if (queued > 0 && first is null)
            events.QueueSynchronization(ServerSynchronization.PurchaseClassificationRequested);
        return Results.Ok(new PurchaseClassificationQueueDto(queued, pending));
    }

    private static async Task<IResult> GetPendingPurchaseClassification(IPurchasesRepository purchases, CastarDbContext db,
        HttpResponse response, CancellationToken token, int limit = 100, bool includeCount = false,
        DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (limit is < 1 or > ClassificationBatchLimit)
            return InvalidInput($"Укажите от 1 до {ClassificationBatchLimit} товаров.");
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");

        if (includeCount)
            await db.Database.OpenConnectionAsync(token);
        await using var snapshot = includeCount ? ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true) : null;
        await using var transaction = snapshot is null ? null : await db.Database.UseTransactionAsync(snapshot, token);
        var pending = await purchases.GetPendingClassificationAsync(limit, token, first, endExclusive);
        if (includeCount)
        {
            var count = await purchases.CountPendingItemsAsync(first, endExclusive, token);
            response.Headers["X-Castar-Classification-Pending"] = count.ToString(CultureInfo.InvariantCulture);
        }
        return Results.Ok(pending.Select(purchase => new PurchaseClassificationWorkDto(
            purchase.PurchaseId, purchase.Revision, purchase.Merchant,
            purchase.Items.Select(item => new PurchaseClassificationItemDto(
                item.ItemId, item.Name, item.HasHistory, item.CategoryId)).ToArray())
        {
            PurchaseName = string.IsNullOrWhiteSpace(purchase.Merchant) ? purchase.PurchaseName : null
        }).ToArray());
    }

    private static bool TryClassificationRange(DateTimeOffset? startUtc, DateTimeOffset? endExclusiveUtc,
        out DateTime? first, out DateTime? endExclusive)
    {
        first = startUtc?.UtcDateTime;
        endExclusive = endExclusiveUtc?.UtcDateTime;
        return first is null && endExclusive is null || first < endExclusive;
    }

    private static Task<IResult> ApplyPurchaseClassification(Guid purchaseId, long revision,
        PurchaseClassificationWriteDto input, IPurchasesRepository purchases, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token) =>
        WritePurchaseClassification(purchaseId, revision, input, purchases, db, events, token, reclassify: false);

    private static Task<IResult> ReclassifyPurchase(Guid purchaseId, long revision,
        PurchaseClassificationWriteDto input, IPurchasesRepository purchases, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token) =>
        WritePurchaseClassification(purchaseId, revision, input, purchases, db, events, token, reclassify: true);

    private static async Task<IResult> WritePurchaseClassification(Guid purchaseId, long revision,
        PurchaseClassificationWriteDto input, IPurchasesRepository purchases, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token, bool reclassify)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию покупки.");
        var limit = reclassify ? PurchaseReclassificationLimit : ClassificationBatchLimit;
        if (input.Items is { } items && items.Count > limit)
            return InvalidInput($"Укажите не более {limit} товаров.");
        if (input.Items is not { Count: > 0 }
            || input.Items.Any(item => item is null || item.ItemId == Guid.Empty)
            || input.Items.Select(item => item.ItemId).Distinct().Count() != input.Items.Count)
            return InvalidInput("Укажите уникальные товары покупки.");

        var categoryIds = input.Items.Select(item => item.CategoryId).OfType<Guid>().Distinct().ToArray();
        if (categoryIds.Length > 0 && await db.BudgetCategories
            .CountAsync(category => category.Kind == BudgetEvents.Purchase && categoryIds.Contains(category.Id), token) != categoryIds.Length)
            return InvalidInput("Выберите существующие категории расходов.");

        var result = await purchases.ApplyClassificationAsync(purchaseId, revision,
            input.Items.ToDictionary(item => item.ItemId, item => item.CategoryId), token, reclassify);
        if (result == PurchaseMutationResult.Applied)
            events.QueueSynchronization(ServerSynchronization.PurchaseClassification);
        return PurchaseMutation(result);
    }

    private static IResult CompleteManualCategoryWrite(PurchaseMutationResult result, CategorySelectionDto? selection,
        CastarDbContext db, Guid purchaseId, BudgetEventPublisher events)
    {
        // A category write uses an explicit transaction with its history. The save interceptor skips it.
        if (selection is not null && result == PurchaseMutationResult.Applied)
        {
            var purchase = db.Purchases.Local.FirstOrDefault(purchase => purchase.Id == purchaseId);
            events.Queue(default(BudgetChange).AddChange(purchase?.AddedOn), default);
        }
        return PurchaseMutation(result);
    }

    private static async Task<IReadOnlyDictionary<Guid, BudgetCategoryDto>> ReadItemCategories(CastarDbContext db,
        IEnumerable<Item> items, CancellationToken token)
    {
        var categoryIds = items.Select(item => item.BudgetCategoryId).OfType<Guid>().Distinct().ToArray();
        return categoryIds.Length == 0 ? new Dictionary<Guid, BudgetCategoryDto>() : await db.BudgetCategories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .Select(category => new BudgetCategoryDto(category.Id, category.Kind, category.Name, category.IconKey, null))
            .ToDictionaryAsync(category => category.Id, token);
    }
}
