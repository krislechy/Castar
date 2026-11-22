using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private static void MapRecurringPurchases(RouteGroupBuilder api)
    {
        var schedules = api.MapGroup("/recurring-purchases");
        schedules.MapGet("", ListRecurringPurchases);
        schedules.MapPost("", CreateRecurringPurchase);
        schedules.MapPut("/{id:guid}", UpdateRecurringPurchase);
        schedules.MapPost("/{id:guid}/pause", PauseRecurringPurchase);
        schedules.MapPost("/{id:guid}/resume", ResumeRecurringPurchase);
        schedules.MapPost("/{id:guid}/skip", SkipRecurringPurchase);
        schedules.MapDelete("/{id:guid}", DeleteRecurringPurchase);
    }

    private static async Task<IResult> ListRecurringPurchases(CastarDbContext db, CancellationToken token)
    {
        var rows = await db.RecurringPurchases.AsNoTracking()
            .OrderBy(row => row.IsPaused).ThenBy(row => row.NextRunUtc).ThenBy(row => row.Id).ToArrayAsync(token);
        var categories = await RecurringPurchaseScheduler.GetCategoryIdsAsync(db, token);
        return Results.Ok(rows.Select(row => RecurringPurchaseScheduler.ToDto(row,
            RecurringPurchaseScheduler.RemoveDeletedCategories(RecurringPurchaseScheduler.Read(row), categories))).ToArray());
    }

    private static Task<IResult> CreateRecurringPurchase(RecurringPurchaseWriteDto input, CastarDbContext db,
        SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        SaveRecurringPurchase(input.Template?.Id ?? Guid.Empty, null, input, db, settings, clock, scheduler, token);

    private static Task<IResult> UpdateRecurringPurchase(Guid id, long revision, RecurringPurchaseWriteDto input,
        CastarDbContext db, SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        SaveRecurringPurchase(id, revision, input, db, settings, clock, scheduler, token);

    private static async Task<IResult> SaveRecurringPurchase(Guid id, long? revision, RecurringPurchaseWriteDto input,
        CastarDbContext db, SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token)
    {
        if (id == Guid.Empty || input.Template is not { Purchase: { } header, Items: { Length: > 0 and <= 250 } items })
            return InvalidInput("Укажите покупку с 1–250 товарами.");
        if (header.UserId is not { } owner || owner == Guid.Empty)
            return InvalidInput("Выберите существующего пользователя.");
        if (!Enum.IsDefined(input.Frequency) || string.IsNullOrWhiteSpace(input.TimeZoneId) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZoneId, out var zone))
            return InvalidInput("Выберите период повторения и действующий часовой пояс.");
        if (ValidatePurchase(header) is { } invalidHeader)
            return invalidHeader;
        try
        {
            var total = 0m;
            foreach (var item in items)
            {
                if (item is null)
                    return InvalidInput("Заполните данные товара.");
                if (ValidatePurchaseItem(item) is { } invalidItem)
                    return invalidItem;
                total += item.Price * (decimal)item.Quantity;
            }
        }
        catch (OverflowException)
        {
            return InvalidInput("Сумма регулярной покупки слишком велика.");
        }

        var localStart = DateTime.SpecifyKind(header.AddedOn, DateTimeKind.Unspecified);
        var policy = settings.ManualCurrencies;
        var currency = header.Currency is null ? policy.DefaultCurrency : ManualCurrencySettings.NormalizeCode(header.Currency);
        var canonicalZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(input.TimeZoneId, out var iana)
            ? iana : input.TimeZoneId;
        var schedule = input with
        {
            TimeZoneId = canonicalZone,
            Template = new(id, header with { Currency = currency, AddedOn = localStart, AddedOnUtc = null }, items)
        };
        var json = RecurringPurchaseScheduler.Serialize(schedule);

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var existing = await db.RecurringPurchases.FindAsync([id], token);
        var previous = existing is null ? null : RecurringPurchaseScheduler.Read(existing);
        if (revision is { } expected)
        {
            if (existing is null)
                return Results.NotFound();
            if (existing.Revision != expected || expected == long.MaxValue)
                return RecurringPurchaseConflict();
        }
        else if (existing is not null)
        {
            return existing.ScheduleJson == json
                ? Results.Ok(RecurringPurchaseScheduler.ToDto(existing, schedule)) : RecurringPurchaseConflict();
        }

        if (currency is null || (!policy.Allows(currency) && currency != previous?.Template.Purchase.Currency))
            return InvalidManualCurrency(policy);
        if (!await db.Users.AnyAsync(user => user.Id == owner, token))
            return InvalidInput("Выберите существующего пользователя.");
        var categories = await RecurringPurchaseScheduler.GetCategoryIdsAsync(db, token);
        if (items.Any(item => item.Category?.Id is { } id && !categories.Contains(id)))
            return InvalidInput("Выберите существующие категории расходов.");

        var changedAnchorOrFrequency = previous is null || previous.Frequency != schedule.Frequency ||
            previous.Template.Purchase.AddedOn != localStart;
        var row = existing ?? new RecurringPurchase { Id = id };
        if (changedAnchorOrFrequency)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var today = BudgetTime.InZone(now, zone).Date;
            if (localStart < today || RecurringPurchaseCalendar.GetUtc(localStart, input.Frequency, zone, 0) is not { } next)
                return InvalidInput("Первая покупка должна быть запланирована на сегодня или на будущую дату.");
            var minimum = existing?.LastRunUtc is { } previousRun &&
                BudgetTime.InZone(previousRun, zone).Date == localStart.Date ? 1 : 0;
            row.NextOccurrence = existing is null ? 0 :
                RecurringPurchaseCalendar.FirstAfter(localStart, input.Frequency, zone, now, minimum);
            row.NextRunUtc = row.NextOccurrence == 0 ? next :
                RecurringPurchaseCalendar.GetUtc(localStart, input.Frequency, zone, row.NextOccurrence);
        }
        else if (previous!.TimeZoneId != schedule.TimeZoneId)
        {
            row.NextRunUtc = RecurringPurchaseCalendar.GetUtc(localStart, input.Frequency, zone, row.NextOccurrence);
        }

        row.UserId = owner;
        row.ScheduleJson = json;
        RecurringPurchaseScheduler.ResetRetry(row);
        if (existing is null)
            db.RecurringPurchases.Add(row);
        else
            row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        scheduler.Changed();
        return Results.Ok(RecurringPurchaseScheduler.ToDto(row, schedule));
    }

    private enum RecurringPurchaseAction { Pause, Resume, Skip, Delete }

    private static Task<IResult> PauseRecurringPurchase(Guid id, long revision, CastarDbContext db,
        TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        ChangeRecurringPurchase(id, revision, RecurringPurchaseAction.Pause, db, clock, scheduler, token);

    private static Task<IResult> ResumeRecurringPurchase(Guid id, long revision, CastarDbContext db,
        TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        ChangeRecurringPurchase(id, revision, RecurringPurchaseAction.Resume, db, clock, scheduler, token);

    private static Task<IResult> SkipRecurringPurchase(Guid id, long revision, CastarDbContext db,
        TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        ChangeRecurringPurchase(id, revision, RecurringPurchaseAction.Skip, db, clock, scheduler, token);

    private static Task<IResult> DeleteRecurringPurchase(Guid id, long revision, CastarDbContext db,
        TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token) =>
        ChangeRecurringPurchase(id, revision, RecurringPurchaseAction.Delete, db, clock, scheduler, token);

    private static async Task<IResult> ChangeRecurringPurchase(Guid id, long revision, RecurringPurchaseAction action,
        CastarDbContext db, TimeProvider clock, RecurringPurchaseScheduler scheduler, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var row = await db.RecurringPurchases.FindAsync([id], token);
        if (row is null)
            return Results.NotFound();
        if (row.Revision != revision || revision == long.MaxValue)
            return RecurringPurchaseConflict();
        if (action == RecurringPurchaseAction.Delete)
        {
            db.RecurringPurchases.Remove(row);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            scheduler.Changed();
            return Results.NoContent();
        }

        var schedule = RecurringPurchaseScheduler.Read(row);
        var anchor = schedule.Template.Purchase.AddedOn;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZoneId, out var zone))
            return InvalidInput("Измените часовой пояс регулярной покупки.");
        switch (action)
        {
            case RecurringPurchaseAction.Pause:
                row.IsPaused = true;
                break;
            case RecurringPurchaseAction.Resume when row.IsPaused:
                row.NextOccurrence = RecurringPurchaseCalendar.FirstAfter(anchor, schedule.Frequency, zone,
                    clock.GetUtcNow().UtcDateTime, row.NextOccurrence);
                row.NextRunUtc = RecurringPurchaseCalendar.GetUtc(anchor, schedule.Frequency, zone, row.NextOccurrence);
                RecurringPurchaseScheduler.ResetRetry(row);
                row.IsPaused = false;
                break;
            case RecurringPurchaseAction.Skip when row.NextRunUtc is not null:
                row.NextRunUtc = RecurringPurchaseCalendar.GetUtc(anchor, schedule.Frequency, zone, ++row.NextOccurrence);
                RecurringPurchaseScheduler.ResetRetry(row);
                break;
        }
        var categories = await RecurringPurchaseScheduler.GetCategoryIdsAsync(db, token);
        row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        scheduler.Changed();
        return Results.Ok(RecurringPurchaseScheduler.ToDto(row,
            RecurringPurchaseScheduler.RemoveDeletedCategories(schedule, categories)));
    }

    private static IResult RecurringPurchaseConflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict,
        title: "Регулярная покупка уже изменилась", detail: "Обновите список и повторите действие.");
}
