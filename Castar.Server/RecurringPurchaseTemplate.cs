using System.Text.Json;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal static class RecurringPurchaseTemplate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Serialize(RecurringPurchaseWriteDto schedule) => JsonSerializer.Serialize(schedule, Json);

    internal static RecurringPurchaseWriteDto Read(RecurringPurchase row) =>
        JsonSerializer.Deserialize<RecurringPurchaseWriteDto>(row.ScheduleJson, Json)
        ?? throw new JsonException("Расписание отсутствует.");

    internal static RecurringPurchaseWriteDto RemoveDeletedCategories(RecurringPurchaseWriteDto schedule,
        IReadOnlySet<Guid> categories) => schedule with
    {
        Template = schedule.Template with
        {
            Items = schedule.Template.Items.Select(item => item.Category?.Id is { } id && !categories.Contains(id)
                ? item with { Category = null } : item).ToArray()
        }
    };

    internal static Task<HashSet<Guid>> GetCategoryIdsAsync(CastarDbContext db, IEnumerable<PurchaseItemWriteDto> items,
        CancellationToken token)
    {
        var ids = items.Select(item => item.Category?.Id).OfType<Guid>().Distinct().ToArray();
        if (ids.Length == 0)
            return Task.FromResult(new HashSet<Guid>());

        var categories = db.BudgetCategories.Where(category => category.Kind == BudgetEvents.Purchase);
        if (ids.Length <= 250)
            categories = categories.Where(category => ids.Contains(category.Id));
        return categories.Select(category => category.Id).ToHashSetAsync(token);
    }

    internal static RecurringPurchaseDto ToDto(RecurringPurchase row, RecurringPurchaseWriteDto schedule) =>
        new(row.Id, schedule, !row.IsPaused && row.NextRunUtc is { } due ? new DateTimeOffset(BudgetTime.AsUtc(due)) : null,
            row.IsPaused, row.Revision)
        {
            RetryAfterUtc = !row.IsPaused && row.RetryAfterUtc is { } retry
                ? new DateTimeOffset(BudgetTime.AsUtc(retry)) : null
        };
}
