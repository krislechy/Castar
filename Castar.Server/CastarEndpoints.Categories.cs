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
            .Select(category => new BudgetCategoryDto(category.Id, category.Kind, category.Name, category.IconKey))
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
        return Results.Json(new BudgetCategoryDto(category.Id, category.Kind, category.Name, category.IconKey),
            statusCode: create ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static Task<bool> IsValidCategoryAsync(CategorySelectionDto? selection, string kind,
        CastarDbContext db, CancellationToken token) => selection?.Id is { } categoryId
        ? db.BudgetCategories.AnyAsync(category => category.Id == categoryId && category.Kind == kind, token)
        : Task.FromResult(true);
}
