using System.Threading.Channels;
using Castar.Api;
using Microsoft.AspNetCore.SignalR;

namespace Castar.Server;

[Flags]
internal enum BudgetChanges
{
    None = 0,
    PurchaseChanged = 1,
    PurchaseCreated = 2,
    IncomeChanged = 4,
    IncomeCreated = 8
}

internal sealed class BudgetEventPublisher(
    IHubContext<BudgetEventsHub> hub, ApiKeyStore keys, ILogger<BudgetEventPublisher> logger) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private int pending;

    public void Queue(BudgetChanges changes)
    {
        if (changes == BudgetChanges.None)
            return;

        // A burst needs one refresh per kind. Preserve creation hints even when updates coalesce.
        Interlocked.Or(ref pending, (int)changes);
        wake.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in wake.Reader.ReadAllAsync(stoppingToken))
        {
            var changes = (BudgetChanges)Interlocked.Exchange(ref pending, 0);
            if ((changes & BudgetChanges.PurchaseChanged) != 0)
                await SendAsync(BudgetEvents.Purchase, (changes & BudgetChanges.PurchaseCreated) != 0, stoppingToken);
            if ((changes & BudgetChanges.IncomeChanged) != 0)
                await SendAsync(BudgetEvents.Income, (changes & BudgetChanges.IncomeCreated) != 0, stoppingToken);
        }
    }

    private async Task SendAsync(string kind, bool created, CancellationToken stoppingToken)
    {
        // An already connected client loses future events when its key is revoked.
        var groups = keys.GetActiveKeyIds().Select(id => id.ToString("N")).ToArray();
        if (groups.Length == 0)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await hub.Clients.Groups(groups).SendAsync(BudgetEvents.Method, new BudgetEventDto(kind, created), timeout.Token);
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
