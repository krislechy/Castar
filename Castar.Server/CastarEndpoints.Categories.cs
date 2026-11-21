using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private const int CategoryNameLength = 100;

    private static async Task<IResult> GetCategories(CastarDbContext db, CancellationToken token) =>
        Results.Ok(await db.BudgetCategories.AsNoTracking()
            .OrderBy(category => category.Kind).ThenBy(category => category.NormalizedName)
            .Select(category => new BudgetCategoryDto(category.Id, category.Kind, category.Name))
            .ToArrayAsync(token));

    private static async Task<IResult> CreateCategory(BudgetCategoryWriteDto input, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        if (input.Kind is not (BudgetEvents.Income or BudgetEvents.Purchase))
            return InvalidInput("Выберите категории доходов или расходов.");
        var name = input.Name?.Trim().Normalize();
        if (string.IsNullOrEmpty(name) || name.Length > CategoryNameLength)
            return InvalidInput($"Укажите название категории длиной до {CategoryNameLength} символов.");

        var category = new BudgetCategory
        {
            Kind = input.Kind,
            Name = name,
            NormalizedName = name.ToUpperInvariant()
        };
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
        return Results.Json(new BudgetCategoryDto(category.Id, category.Kind, category.Name),
            statusCode: StatusCodes.Status201Created);
    }

    private static Task<bool> IsValidCategoryAsync(CategorySelectionDto? selection, string kind,
        CastarDbContext db, CancellationToken token) => selection?.Id is { } categoryId
        ? db.BudgetCategories.AnyAsync(category => category.Id == categoryId && category.Kind == kind, token)
        : Task.FromResult(true);
}
