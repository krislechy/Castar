using System.Globalization;
using Castar.Api;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private static async Task<IResult> CountUncategorizedIncomes(IIncomesRepository incomes, CancellationToken token,
        DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");
        return Results.Ok(new ClassificationCountDto(await incomes.CountUncategorizedAsync(first, endExclusive, token)));
    }

    private static async Task<IResult> QueueUncategorizedIncomes(IIncomesRepository incomes, BudgetEventPublisher events,
        CancellationToken token, DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");
        var (queued, pending) = await incomes.QueueUncategorizedAsync(first, endExclusive, token);
        if (queued > 0 && first is null)
            events.QueueSynchronization(ServerSynchronization.IncomeClassificationRequested);
        return Results.Ok(new PurchaseClassificationQueueDto(queued, pending));
    }

    private static async Task<IResult> GetPendingIncomeClassification(IIncomesRepository incomes, CastarDbContext db,
        HttpResponse response, CancellationToken token, int limit = 100, bool includeCount = false,
        DateTimeOffset? startUtc = null, DateTimeOffset? endExclusiveUtc = null)
    {
        if (limit is < 1 or > ClassificationBatchLimit)
            return InvalidInput($"Укажите от 1 до {ClassificationBatchLimit} доходов.");
        if (!TryClassificationRange(startUtc, endExclusiveUtc, out var first, out var endExclusive))
            return InvalidInput("Укажите допустимый период.");

        if (includeCount)
            await db.Database.OpenConnectionAsync(token);
        await using var snapshot = includeCount ? ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true) : null;
        await using var transaction = snapshot is null ? null : await db.Database.UseTransactionAsync(snapshot, token);
        var pending = await incomes.GetPendingClassificationAsync(limit, first, endExclusive, token);
        if (includeCount)
        {
            var count = await incomes.CountPendingClassificationAsync(first, endExclusive, token);
            response.Headers["X-Castar-Classification-Pending"] = count.ToString(CultureInfo.InvariantCulture);
        }
        return Results.Ok(pending.Select(income => new IncomeClassificationWorkDto(income.IncomeId, income.Revision,
            income.Source, income.HasHistory, income.CategoryId)).ToArray());
    }

    private static async Task<IResult> ApplyIncomeClassification(Guid id, long revision, CategorySelectionDto input,
        IIncomesRepository incomes, CastarDbContext db, BudgetEventPublisher events, CancellationToken token)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию дохода.");
        if (!await IsValidCategoryAsync(input, BudgetEvents.Income, db, token))
            return InvalidInput("Выберите существующую категорию доходов.");

        switch (await incomes.ApplyClassificationAsync(id, revision, input.Id, token))
        {
            case IncomeClassificationResult.Applied:
                events.QueueSynchronization(ServerSynchronization.IncomeClassification);
                return Results.NoContent();
            case IncomeClassificationResult.NotFound:
                return Results.NotFound();
            case IncomeClassificationResult.Conflict:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Доход уже изменился");
            default:
                return Results.NoContent();
        }
    }
}
