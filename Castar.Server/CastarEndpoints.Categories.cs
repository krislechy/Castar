using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private const int CategoryNameLength = 100;

    private static async Task<IResult> GetCategories(CastarDbContext db, CancellationToken token)
    {
        var categories = await db.BudgetCategories.AsNoTracking()
            .OrderBy(category => category.Kind).ThenBy(category => category.NormalizedName)
            .Select(category => new BudgetCategoryDto(category.Id, category.Kind, category.Name, category.IconKey, category.Tags))
            .ToArrayAsync(token);
        return Results.Ok(categories.Select(category => category with
        {
            IconKey = BudgetCategoryIconKeys.Canonicalize(category.Kind, category.IconKey)
        }).ToArray());
    }

    private static async Task<IResult> CreateCategory(BudgetCategoryWriteDto input, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        var category = new BudgetCategory { Kind = input.Kind };
        return await SaveCategory(input, category, create: true, db, events, token);
    }

    private static async Task<IResult> UpdateCategory(Guid id, BudgetCategoryWriteDto input, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        var category = await db.BudgetCategories.SingleOrDefaultAsync(category => category.Id == id, token);
        if (category is null)
            return Results.NotFound();
        return await SaveCategory(input, category, create: false, db, events, token);
    }

    private static async Task<IResult> DeleteCategory(Guid id, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var category = db.BudgetCategories.Where(category => category.Id == id);
        if (!await category.AnyAsync(token))
            return Results.NotFound();

        var purchases = db.Purchases.Where(purchase => purchase.CategoryId == id ||
            purchase.Items.Any(item => item.BudgetCategoryId == id));
        var incomes = db.Incomes.Where(income => income.CategoryId == id);
        if (await purchases.AnyAsync(purchase => purchase.Revision == long.MaxValue, token) ||
            await incomes.AnyAsync(income => income.CategoryRevision == long.MaxValue, token))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "Достигнут предел версии связанной записи.");

        var now = DateTime.UtcNow;
        // Find and revise purchases while their item links are still present.
        var changedPurchases = await purchases.ExecuteUpdateAsync(update => update
            .SetProperty(purchase => purchase.CategoryId, purchase => purchase.CategoryId == id ? null : purchase.CategoryId)
            .SetProperty(purchase => purchase.Revision, purchase => purchase.Revision + 1)
            .SetProperty(purchase => purchase.ModifiedOn, now), token);
        await db.Items.Where(item => item.BudgetCategoryId == id).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.BudgetCategoryId, (Guid?)null)
            .SetProperty(item => item.CategoryState, ItemCategoryState.Archived)
            .SetProperty(item => item.ModifiedOn, now), token);
        var changedIncomes = await incomes.ExecuteUpdateAsync(update => update
            .SetProperty(income => income.CategoryId, (Guid?)null)
            .SetProperty(income => income.CategoryState, IncomeCategoryState.Archived)
            .SetProperty(income => income.CategoryRevision, income => income.CategoryRevision + 1)
            .SetProperty(income => income.ModifiedOn, now), token);
        await db.ItemCategoryHistory.Where(entry => entry.BudgetCategoryId == id).ExecuteDeleteAsync(token);
        await db.IncomeCategoryHistory.Where(entry => entry.CategoryId == id).ExecuteDeleteAsync(token);
        await category.ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);

        events.QueueSynchronization(ServerSynchronization.Categories);
        if (changedPurchases > 0)
            events.QueueSynchronization(ServerSynchronization.PurchaseClassification);
        if (changedIncomes > 0)
            events.QueueSynchronization(ServerSynchronization.IncomeClassification);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveCategory(BudgetCategoryWriteDto input, BudgetCategory category, bool create,
        CastarDbContext db, BudgetEventPublisher events, CancellationToken token)
    {
        if (input.Kind is not (BudgetEvents.Income or BudgetEvents.Purchase) || category.Kind != input.Kind)
            return InvalidInput("Выберите категории доходов или расходов.");
        var name = input.Name?.Trim().Normalize();
        if (string.IsNullOrEmpty(name) || name.Length > CategoryNameLength)
            return InvalidInput($"Укажите название категории длиной до {CategoryNameLength} символов.");
        var requestedIcon = string.IsNullOrWhiteSpace(input.IconKey) ? null : input.IconKey.Trim();
        var icon = BudgetCategoryIconKeys.Canonicalize(input.Kind, requestedIcon);
        if (requestedIcon is not null && icon is null)
            return InvalidInput("Выберите значок из списка.");
        if (input.Tags is { } tags)
        {
            if (NormalizeCategoryTags(tags, out var normalized) is { } tagError)
                return InvalidInput(tagError);
            category.Tags = normalized;
        }

        category.Name = name;
        category.NormalizedName = name.ToUpperInvariant();
        category.IconKey = icon;
        if (create)
            db.BudgetCategories.Add(category);
        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Категория уже существует", detail: "Укажите другое название категории.");
        }

        events.QueueSynchronization(ServerSynchronization.Categories);
        return Results.Json(new BudgetCategoryDto(category.Id, category.Kind, category.Name, category.IconKey, category.Tags),
            statusCode: create ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static string? NormalizeCategoryTags(IReadOnlyList<string> tags, out string[] normalized)
    {
        normalized = [];
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string>();
        foreach (var tag in tags)
        {
            var value = tag?.Trim().Normalize();
            if (string.IsNullOrEmpty(value) || value.Length > BudgetCategoryTagLimits.MaxLength || value.Any(char.IsControl))
                return $"Укажите непустой тег длиной до {BudgetCategoryTagLimits.MaxLength} символов без переноса строки.";
            if (!unique.Add(value))
                continue;
            if (values.Count == BudgetCategoryTagLimits.MaxCount)
                return $"Для категории можно задать не более {BudgetCategoryTagLimits.MaxCount} тегов.";
            values.Add(value);
        }

        normalized = values.ToArray();
        return null;
    }

    private static Task<bool> IsValidCategoryAsync(CategorySelectionDto? selection, string kind,
        CastarDbContext db, CancellationToken token) => selection?.Id is { } categoryId
        ? db.BudgetCategories.AnyAsync(category => category.Id == categoryId && category.Kind == kind, token)
        : Task.FromResult(true);
}
