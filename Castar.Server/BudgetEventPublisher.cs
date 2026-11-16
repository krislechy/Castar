using System.Threading.Channels;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.DataBase;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal readonly record struct BudgetChange(bool Changed, int CreatedCount, Guid? EntityId, DateTime? AddedOn)
{
    public BudgetChange AddCreation(Guid id, DateTime? addedOn) =>
        new(true, CreatedCount + 1, id, addedOn);

    public BudgetChange Merge(BudgetChange newer) => newer.CreatedCount > 0
        ? newer with { CreatedCount = CreatedCount + newer.CreatedCount }
        : this with { Changed = Changed || newer.Changed };
}

internal sealed class BudgetEventPublisher(
    IHubContext<BudgetEventsHub> hub, ApiKeyStore keys,
    IServiceScopeFactory scopes, ILogger<BudgetEventPublisher> logger) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock gate = new();
    private BudgetChange pendingPurchases;
    private BudgetChange pendingIncomes;
    private bool pendingUsers;
    private long? pendingSettingsRevision;

    public void Queue(BudgetChange purchases, BudgetChange incomes)
    {
        if (!purchases.Changed && !incomes.Changed)
            return;

        lock (gate)
        {
            pendingPurchases = pendingPurchases.Merge(purchases);
            pendingIncomes = pendingIncomes.Merge(incomes);
            wake.Writer.TryWrite(true);
        }
    }

    public void QueueSynchronization(string kind, long? revision = null)
    {
        lock (gate)
        {
            switch (kind)
            {
                case ServerSynchronization.Users:
                    pendingUsers = true;
                    break;
                case ServerSynchronization.Settings when revision is { } version:
                    pendingSettingsRevision = Math.Max(pendingSettingsRevision ?? version, version);
                    break;
                default:
                    throw new ArgumentException("Unsupported synchronization event.", nameof(kind));
            }
            wake.Writer.TryWrite(true);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in wake.Reader.ReadAllAsync(stoppingToken))
        {
            BudgetChange purchases, incomes;
            bool users;
            long? settingsRevision;
            lock (gate)
            {
                purchases = pendingPurchases;
                incomes = pendingIncomes;
                users = pendingUsers;
                settingsRevision = pendingSettingsRevision;
                pendingPurchases = default;
                pendingIncomes = default;
                pendingUsers = false;
                pendingSettingsRevision = null;
            }

            if (purchases.Changed)
                await SendAsync(BudgetEvents.Purchase, purchases, stoppingToken);
            if (incomes.Changed)
                await SendAsync(BudgetEvents.Income, incomes, stoppingToken);
            if (users)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.Users), stoppingToken);
            if (settingsRevision is { } revision)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.Settings, revision), stoppingToken);
        }
    }

    private async Task SendAsync(string kind, BudgetChange change, CancellationToken stoppingToken)
    {
        var preview = change.CreatedCount > 0 && change.EntityId is { } id
            ? await ReadPreviewAsync(kind, id, stoppingToken)
            : null;
        var notification = new BudgetEventDto(kind, change.CreatedCount > 0)
        {
            CreatedCount = change.CreatedCount,
            EntityId = change.EntityId,
            AddedOn = change.AddedOn is { } date ? BudgetTime.InZone(date, BudgetTime.Belgrade) : null,
            AddedOnUtc = change.AddedOn is { } instant ? new DateTimeOffset(BudgetTime.AsUtc(instant)) : null,
            Preview = preview
        };
        await SendToActiveKeysAsync(BudgetEvents.Method, notification, stoppingToken);
    }

    private async Task SendToActiveKeysAsync<T>(string method, T message, CancellationToken stoppingToken)
    {
        // An already connected client loses future events when its key is revoked.
        var groups = keys.GetActiveKeyIds().Select(id => id.ToString("N")).ToArray();
        if (groups.Length == 0)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await hub.Clients.Groups(groups).SendAsync(method, message, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
                logger.LogDebug("Timed out sending a budget change notification.");
        }
        catch (Exception error)
        {
            logger.LogWarning("Could not send a budget change notification: {Type}", error.GetType().Name);
        }
    }

    private async Task<BudgetOperationPreviewDto?> ReadPreviewAsync(string kind, Guid id, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            // Read the committed operation in a fresh scope, without loading its detail or avatar bytes.
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
            database.Database.SetCommandTimeout(1);
            return kind == BudgetEvents.Purchase
                ? await database.Purchases.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new BudgetOperationPreviewDto(x.UserId, x.User.Name, x.Name, x.TotalSum, x.Currency,
                        x.User.ImageRaw != null && x.User.ImageRaw.Length != 0)).SingleOrDefaultAsync(timeout.Token)
                : await database.Incomes.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new BudgetOperationPreviewDto(x.UserId, x.User.Name, x.Source, x.Sum, x.Currency,
                        x.User.ImageRaw != null && x.User.ImageRaw.Length != 0)).SingleOrDefaultAsync(timeout.Token);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not load a budget notification preview: {Type}", error.GetType().Name);
            return null;
        }
    }
}
