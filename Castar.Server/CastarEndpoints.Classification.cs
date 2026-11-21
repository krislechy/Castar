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

    private static async Task<IResult> QueueUncategorizedPurchaseItems(IPurchasesRepository purchases,
        BudgetEventPublisher events, CancellationToken token)
    {
        var (queued, pending) = await purchases.QueueUncategorizedItemsAsync(token);
        if (queued > 0)
            events.QueueSynchronization(ServerSynchronization.PurchaseClassificationRequested);
        return Results.Ok(new PurchaseClassificationQueueDto(queued, pending));
    }

    private static async Task<IResult> GetPendingPurchaseClassification(IPurchasesRepository purchases, CastarDbContext db,
        HttpResponse response, CancellationToken token, int limit = 100, bool includeCount = false)
    {
        if (limit is < 1 or > ClassificationBatchLimit)
            return InvalidInput($"Укажите от 1 до {ClassificationBatchLimit} товаров.");

        if (includeCount)
            await db.Database.OpenConnectionAsync(token);
        await using var snapshot = includeCount ? ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true) : null;
        await using var transaction = snapshot is null ? null : await db.Database.UseTransactionAsync(snapshot, token);
        var pending = await purchases.GetPendingClassificationAsync(limit, token);
        if (includeCount)
        {
            var count = await db.Items.CountAsync(item => item.CategoryState == ItemCategoryState.Pending, token);
            response.Headers["X-Castar-Classification-Pending"] = count.ToString(CultureInfo.InvariantCulture);
        }
        return Results.Ok(pending.Select(purchase => new PurchaseClassificationWorkDto(
            purchase.PurchaseId, purchase.Revision, purchase.Merchant,
            purchase.Items.Select(item => new PurchaseClassificationItemDto(
                item.ItemId, item.Name, item.HasHistory, item.CategoryId)).ToArray())).ToArray());
    }

    private static async Task<IResult> ApplyPurchaseClassification(Guid purchaseId, long revision,
        PurchaseClassificationWriteDto input, IPurchasesRepository purchases, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию покупки.");
        if (input.Items is not { Count: > 0 and <= ClassificationBatchLimit }
            || input.Items.Any(item => item is null || item.ItemId == Guid.Empty)
            || input.Items.Select(item => item.ItemId).Distinct().Count() != input.Items.Count)
            return InvalidInput("Укажите уникальные товары покупки.");

        var categoryIds = input.Items.Select(item => item.CategoryId).OfType<Guid>().Distinct().ToArray();
        if (categoryIds.Length > 0 && await db.BudgetCategories
            .CountAsync(category => category.Kind == BudgetEvents.Purchase && categoryIds.Contains(category.Id), token) != categoryIds.Length)
            return InvalidInput("Выберите существующие категории расходов.");

        var result = await purchases.ApplyClassificationAsync(purchaseId, revision,
            input.Items.ToDictionary(item => item.ItemId, item => item.CategoryId), token);
        if (result == PurchaseMutationResult.Applied)
            events.QueueSynchronization(ServerSynchronization.PurchaseClassification);
        return PurchaseMutation(result);
    }

    private static IResult CompleteManualCategoryWrite(PurchaseMutationResult result, CategorySelectionDto? selection,
        BudgetEventPublisher events)
    {
        // A category write uses an explicit transaction with its history. The save interceptor skips it.
        if (selection is not null && result == PurchaseMutationResult.Applied)
            events.Queue(new BudgetChange(true, 0, null, null), default);
        return PurchaseMutation(result);
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ReadItemCategoryNames(CastarDbContext db,
        IEnumerable<Item> items, CancellationToken token)
    {
        var categoryIds = items.Select(item => item.BudgetCategoryId).OfType<Guid>().Distinct().ToArray();
        return categoryIds.Length == 0 ? new Dictionary<Guid, string>() : await db.BudgetCategories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id)).ToDictionaryAsync(category => category.Id, category => category.Name, token);
    }
}
