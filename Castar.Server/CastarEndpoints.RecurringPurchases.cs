using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Microsoft.EntityFrameworkCore;
using TimeZoneConverter;

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

    private static async Task<IResult> ListRecurringPurchases(CastarDbContext db, HttpRequest request, CancellationToken token)
    {
        var rows = await db.RecurringPurchases.AsNoTracking()
            .OrderBy(row => row.IsPaused).ThenBy(row => row.NextRunUtc).ThenBy(row => row.Id).ToArrayAsync(token);
        var schedules = rows.Select(row => (Row: row, Schedule: RecurringPurchaseTemplate.Read(row))).ToArray();
        if (!SupportsItemCurrency(request) && schedules.Any(entry => ContainsForeignItemCurrency(entry.Schedule.Template)))
            return ItemCurrencyClientRequired();
        var categories = await RecurringPurchaseTemplate.GetCategoryIdsAsync(db, schedules.SelectMany(entry => entry.Schedule.Template.Items), token);
        return Results.Ok(schedules.Select(entry => RecurringPurchaseTemplate.ToDto(entry.Row,
            RecurringPurchaseTemplate.RemoveDeletedCategories(entry.Schedule, categories))).ToArray());
    }

    private static Task<IResult> CreateRecurringPurchase(RecurringPurchaseWriteDto input, CastarDbContext db,
        SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler, HttpRequest request,
        CancellationToken token) =>
        SaveRecurringPurchase(input.Template?.Id ?? Guid.Empty, null, input, db, settings, clock, scheduler, request, token);

    private static Task<IResult> UpdateRecurringPurchase(Guid id, long revision, RecurringPurchaseWriteDto input,
        CastarDbContext db, SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler,
        HttpRequest request, CancellationToken token) =>
        SaveRecurringPurchase(id, revision, input, db, settings, clock, scheduler, request, token);

    private static async Task<IResult> SaveRecurringPurchase(Guid id, long? revision, RecurringPurchaseWriteDto input,
        CastarDbContext db, SettingsModel settings, TimeProvider clock, RecurringPurchaseScheduler scheduler,
        HttpRequest request, CancellationToken token)
    {
        if (id == Guid.Empty || input.Template is not { Purchase: { } header, Items: { Length: > 0 and <= 250 } items })
            return InvalidInput("Укажите покупку с 1–250 товарами.");
        var supportsItemCurrency = SupportsItemCurrency(request);
        if (!supportsItemCurrency && items.Any(item => item?.Currency is not null))
            return ItemCurrencyClientRequired();
        if (header.UserId is not { } owner || owner == Guid.Empty)
            return InvalidInput("Выберите существующего пользователя.");
        if (!Enum.IsDefined(input.Frequency))
            return InvalidInput("Выберите период повторения.");
        string? canonicalZone = null;
        if (input.ZoneRuleId is null && input.UtcOffsetMinutes is null && !string.IsNullOrWhiteSpace(input.TimeZoneId))
            canonicalZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(input.TimeZoneId, out var iana) ||
                TZConvert.TryWindowsToIana(input.TimeZoneId, out iana) ? iana : input.TimeZoneId;
        if (!RecurringPurchaseTime.TryResolve(input with { TimeZoneId = canonicalZone }, out var zone))
            return InvalidInput("Выберите доступный часовой пояс или смещение UTC от −14:00 до +14:00.");
        if (ValidatePurchase(header) is { } invalidHeader)
            return invalidHeader;
        foreach (var item in items)
        {
            if (item is null)
                return InvalidInput("Заполните данные товара.");
            if (ValidatePurchaseItem(item) is { } invalidItem)
                return invalidItem;
        }

        var localStart = DateTime.SpecifyKind(header.AddedOn, DateTimeKind.Unspecified);
        var policy = settings.ManualCurrencies;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var existing = await db.RecurringPurchases.FindAsync([id], token);
        var previous = existing is null ? null : RecurringPurchaseTemplate.Read(existing);
        if (!supportsItemCurrency && previous is not null && ContainsForeignItemCurrency(previous.Template))
            return ItemCurrencyClientRequired();
        var previousHeaderCurrency = previous?.Template.Purchase.Currency;
        var itemsOnly = header.Currency is null && previousHeaderCurrency is null &&
            (items.Any(item => item.Currency is not null) || previous is not null && ContainsForeignItemCurrency(previous.Template));
        var currency = itemsOnly ? null : header.Currency is null
            ? previousHeaderCurrency ?? policy.DefaultCurrency : ManualCurrencySettings.NormalizeCode(header.Currency);
        if (previous is not null && ContainsForeignItemCurrency(previous.Template) && currency != previousHeaderCurrency)
            return InvalidInput("Для этой подписки валюту можно изменить в товарах.");
        var normalizedItems = new PurchaseItemWriteDto[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var itemCurrency = ManualCurrencySettings.NormalizeCode(items[index].Currency);
            if (items[index].Currency is not null && itemCurrency is null)
                return InvalidManualCurrency(policy);
            normalizedItems[index] = items[index] with
            {
                Currency = itemsOnly ? itemCurrency ?? policy.DefaultCurrency : itemCurrency == currency ? null : itemCurrency
            };
        }
        var schedule = input with
        {
            TimeZoneId = canonicalZone,
            UtcOffsetMinutes = input.ZoneRuleId is null ? input.UtcOffsetMinutes :
                (int)zone.GetUtcOffset(localStart).TotalMinutes,
            Template = new(id, header with { Currency = currency, AddedOn = localStart, AddedOnUtc = null }, normalizedItems)
        };
        var json = RecurringPurchaseTemplate.Serialize(schedule);

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
                ? Results.Ok(RecurringPurchaseTemplate.ToDto(existing, schedule)) : RecurringPurchaseConflict();
        }

        if (!itemsOnly && (currency is null || !policy.Allows(currency) && currency != previousHeaderCurrency))
            return InvalidManualCurrency(policy);
        var previousItems = previous?.Template.Items ?? [];
        for (var index = 0; index < normalizedItems.Length; index++)
        {
            var itemCurrency = normalizedItems[index].Currency;
            if (itemCurrency is not null && !policy.Allows(itemCurrency) &&
                (index >= previousItems.Length || previousItems[index].Currency != itemCurrency))
                return InvalidManualCurrency(policy);
        }
        if (!await db.Users.AnyAsync(user => user.Id == owner, token))
            return InvalidInput("Выберите существующего пользователя.");
        var categories = await RecurringPurchaseTemplate.GetCategoryIdsAsync(db, items, token);
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
        else if (previous!.TimeZoneId != schedule.TimeZoneId || previous.ZoneRuleId != schedule.ZoneRuleId ||
            schedule.ZoneRuleId is null && previous.UtcOffsetMinutes != schedule.UtcOffsetMinutes)
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
        return Results.Ok(RecurringPurchaseTemplate.ToDto(row, schedule));
    }

    private static bool ContainsForeignItemCurrency(PurchaseCreateDto template) =>
        template.Items.Any(item => item.Currency is not null && item.Currency != template.Purchase.Currency);

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

        var schedule = RecurringPurchaseTemplate.Read(row);
        var anchor = schedule.Template.Purchase.AddedOn;
        if (!RecurringPurchaseTime.TryResolve(schedule, out var zone))
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
        var categories = await RecurringPurchaseTemplate.GetCategoryIdsAsync(db, schedule.Template.Items, token);
        row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        scheduler.Changed();
        return Results.Ok(RecurringPurchaseTemplate.ToDto(row,
            RecurringPurchaseTemplate.RemoveDeletedCategories(schedule, categories)));
    }

    private static IResult RecurringPurchaseConflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict,
        title: "Регулярная покупка уже изменилась", detail: "Обновите список и повторите действие.");
}
