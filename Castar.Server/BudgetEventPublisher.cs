using System.Threading.Channels;
using Castar.Api;
using Microsoft.AspNetCore.SignalR;

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
    IHubContext<BudgetEventsHub> hub, ApiKeyStore keys, ILogger<BudgetEventPublisher> logger) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock gate = new();
    private BudgetChange pendingPurchases;
    private BudgetChange pendingIncomes;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in wake.Reader.ReadAllAsync(stoppingToken))
        {
            BudgetChange purchases, incomes;
            lock (gate)
            {
                purchases = pendingPurchases;
                incomes = pendingIncomes;
                pendingPurchases = default;
                pendingIncomes = default;
            }

            if (purchases.Changed)
                await SendAsync(BudgetEvents.Purchase, purchases, stoppingToken);
            if (incomes.Changed)
                await SendAsync(BudgetEvents.Income, incomes, stoppingToken);
        }
    }

    private async Task SendAsync(string kind, BudgetChange change, CancellationToken stoppingToken)
    {
        // An already connected client loses future events when its key is revoked.
        var groups = keys.GetActiveKeyIds().Select(id => id.ToString("N")).ToArray();
        if (groups.Length == 0)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var notification = new BudgetEventDto(kind, change.CreatedCount > 0)
            {
                CreatedCount = change.CreatedCount,
                EntityId = change.EntityId,
                AddedOn = change.AddedOn
            };
            await hub.Clients.Groups(groups).SendAsync(BudgetEvents.Method, notification, timeout.Token);
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
}
