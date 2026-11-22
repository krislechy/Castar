using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Castar.Api;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal sealed class RecurringPurchaseScheduler(IServiceScopeFactory scopes, BudgetEventPublisher events,
    TimeProvider clock, ILogger<RecurringPurchaseScheduler> logger, Func<bool> activationPending) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

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

    internal static Task<HashSet<Guid>> GetCategoryIdsAsync(CastarDbContext db, CancellationToken token) =>
        db.BudgetCategories.Where(category => category.Kind == BudgetEvents.Purchase).Select(category => category.Id)
            .ToHashSetAsync(token);

    internal static RecurringPurchaseDto ToDto(RecurringPurchase row, RecurringPurchaseWriteDto schedule) =>
        new(row.Id, schedule, !row.IsPaused && row.NextRunUtc is { } due ? new DateTimeOffset(BudgetTime.AsUtc(due)) : null,
            row.IsPaused, row.Revision)
        {
            RetryAfterUtc = !row.IsPaused && row.RetryAfterUtc is { } retry
                ? new DateTimeOffset(BudgetTime.AsUtc(retry)) : null
        };

    internal static void ResetRetry(RecurringPurchase row)
    {
        row.RetryCount = 0;
        row.RetryAfterUtc = null;
    }

    internal void Changed()
    {
        events.QueueSynchronization(ServerSynchronization.RecurringPurchases);
        changes.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (activationPending())
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken);

        var consecutive = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (changes.Reader.TryRead(out _)) { }
                var next = await RunNextAsync(stoppingToken);
                if (next is { } due && due <= clock.GetUtcNow().UtcDateTime)
                {
                    // Leave a chance for user edits and other database writers when catching up after downtime.
                    if (++consecutive == 32)
                    {
                        consecutive = 0;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stoppingToken);
                    }
                    continue;
                }
                consecutive = 0;
                await WaitAsync(next, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Планировщик регулярных покупок не смог обратиться к базе ({ErrorType}).", error.GetType().Name);
                await WaitAsync(clock.GetUtcNow().UtcDateTime.AddMinutes(1), stoppingToken);
            }
        }
    }

    private async Task WaitAsync(DateTime? due, CancellationToken token)
    {
        if (due is null)
        {
            await changes.Reader.ReadAsync(token);
            return;
        }

        var delay = due.Value - clock.GetUtcNow().UtcDateTime;
        if (delay <= TimeSpan.Zero)
            return;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, clock, wait.Token);
        var changed = changes.Reader.ReadAsync(wait.Token).AsTask();
        await Task.WhenAny(timer, changed);
        await wait.CancelAsync();
        token.ThrowIfCancellationRequested();
    }

    private async Task<DateTime?> RunNextAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var next = await db.RecurringPurchases.Where(row => !row.IsPaused && row.NextRunUtc != null)
            .OrderBy(row => row.RetryAfterUtc ?? row.NextRunUtc)
            .ThenBy(row => row.NextRunUtc).ThenBy(row => row.Id)
            .Select(row => new ScheduledRun(row.Id, row.NextRunUtc!.Value, row.RetryAfterUtc, row.RetryCount, row.Revision))
            .FirstOrDefaultAsync(token);
        if (next is null || next.ReadyAt > clock.GetUtcNow().UtcDateTime)
            return next?.ReadyAt;

        (bool Processed, Guid? PurchaseId) result;
        try
        {
            result = await SaveOccurrenceAsync(db, scope.ServiceProvider, next, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            // The failed write has rolled back. Persist just the retry state and leave the original run due.
            await DeferOccurrenceAsync(next, error, token);
            return next.ReadyAt;
        }
        if (result.PurchaseId is { } purchaseId)
            events.Queue(default(BudgetChange).AddCreation(purchaseId, next.Due), default);
        if (result.Processed)
            events.QueueSynchronization(ServerSynchronization.RecurringPurchases);
        return next.ReadyAt;
    }

    private static IQueryable<RecurringPurchase> UnchangedOccurrence(CastarDbContext db, ScheduledRun run) =>
        db.RecurringPurchases.Where(row => row.Id == run.Id && row.Revision == run.Revision &&
            !row.IsPaused && row.NextRunUtc == run.Due && row.RetryCount == run.RetryCount &&
            row.RetryAfterUtc == run.RetryAfter);

    private async Task DeferOccurrenceAsync(ScheduledRun run, Exception error, CancellationToken token)
    {
        var count = Math.Min(run.RetryCount + 1, 9);
        var retryAfter = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 15 * (1 << (count - 1))));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var changed = await UnchangedOccurrence(db, run).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.RetryCount, count)
            .SetProperty(row => row.RetryAfterUtc, retryAfter), token);
        if (changed == 0)
            return;
        logger.LogWarning("Регулярная покупка {ScheduleId} за {DueUtc:O} отложена до {RetryAfterUtc:O} ({ErrorType}).",
            run.Id, run.Due, retryAfter, error.GetType().Name);
        events.QueueSynchronization(ServerSynchronization.RecurringPurchases);
    }

    private static async Task<(bool Processed, Guid? PurchaseId)> SaveOccurrenceAsync(CastarDbContext db,
        IServiceProvider services, ScheduledRun run, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var row = await UnchangedOccurrence(db, run).FirstOrDefaultAsync(token);
        if (row is null)
            return (false, null);

        var due = run.Due;
        var schedule = Read(row);
        var header = schedule.Template.Purchase;
        var purchaseId = PurchaseId(row.Id, due);
        var created = false;
        if (!RecurringPurchaseTime.TryResolve(schedule, out var zone))
            throw new TimeZoneNotFoundException();
        if (!await db.Users.AnyAsync(user => user.Id == row.UserId, token))
            throw new InvalidOperationException("Владелец регулярной покупки недоступен.");
        if (!await db.Purchases.AnyAsync(purchase => purchase.Id == purchaseId, token))
        {
            var categories = await GetCategoryIdsAsync(db, token);
            var purchase = new Purchase
            {
                Id = purchaseId, UserId = row.UserId, Name = header.Name, Currency = header.Currency,
                AddedOn = due, RateDate = BudgetTime.BelgradeDate(due),
                RetailPlace = header.RetailPlace, RetailPlaceAddress = header.RetailPlaceAddress
            };
            foreach (var item in RemoveDeletedCategories(schedule, categories).Template.Items)
                purchase.Items.Add(new Item
                {
                    Name = item.Name, Quantity = item.Quantity, Price = item.Price, Currency = item.Currency,
                    BudgetCategoryId = item.Category?.Id,
                    CategoryState = item.Category is null ? ItemCategoryState.Pending : ItemCategoryState.Confirmed
                });
            await services.GetRequiredService<IPurchasesRepository>().Add(purchase, token);
            created = true;
        }
        row.LastRunUtc = due;
        row.NextRunUtc = RecurringPurchaseCalendar.GetUtc(header.AddedOn, schedule.Frequency, zone, ++row.NextOccurrence);
        ResetRetry(row);
        row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return (true, created ? purchaseId : null);
    }

    private sealed record ScheduledRun(Guid Id, DateTime Due, DateTime? RetryAfter, int RetryCount, long Revision)
    {
        internal DateTime ReadyAt => RetryAfter ?? Due;
    }

    private static Guid PurchaseId(Guid scheduleId, DateTime utc)
    {
        Span<byte> occurrence = stackalloc byte[24];
        scheduleId.TryWriteBytes(occurrence);
        BinaryPrimitives.WriteInt64LittleEndian(occurrence[16..], utc.Ticks);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(occurrence, hash);
        return new Guid(hash[..16]);
    }
}
