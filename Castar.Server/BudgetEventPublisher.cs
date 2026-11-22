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
    private bool pendingCategories;
    private bool pendingRecurringPurchases;
    private bool pendingPurchaseClassification;
    private bool pendingPurchaseClassificationRequest;
    private bool pendingIncomeClassification;
    private bool pendingIncomeClassificationRequest;
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
                case ServerSynchronization.Categories:
                    pendingCategories = true;
                    break;
                case ServerSynchronization.RecurringPurchases:
                    pendingRecurringPurchases = true;
                    break;
                case ServerSynchronization.PurchaseClassification:
                    pendingPurchaseClassification = true;
                    break;
                case ServerSynchronization.PurchaseClassificationRequested:
                    pendingPurchaseClassificationRequest = true;
                    break;
                case ServerSynchronization.IncomeClassification:
                    pendingIncomeClassification = true;
                    break;
                case ServerSynchronization.IncomeClassificationRequested:
                    pendingIncomeClassificationRequest = true;
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
            bool users, categories, recurringPurchases, purchaseClassification, purchaseClassificationRequest,
                incomeClassification, incomeClassificationRequest;
            long? settingsRevision;
            lock (gate)
            {
                purchases = pendingPurchases;
                incomes = pendingIncomes;
                users = pendingUsers;
                categories = pendingCategories;
                recurringPurchases = pendingRecurringPurchases;
                purchaseClassification = pendingPurchaseClassification;
                purchaseClassificationRequest = pendingPurchaseClassificationRequest;
                incomeClassification = pendingIncomeClassification;
                incomeClassificationRequest = pendingIncomeClassificationRequest;
                settingsRevision = pendingSettingsRevision;
                pendingPurchases = default;
                pendingIncomes = default;
                pendingUsers = false;
                pendingCategories = false;
                pendingRecurringPurchases = false;
                pendingPurchaseClassification = false;
                pendingPurchaseClassificationRequest = false;
                pendingIncomeClassification = false;
                pendingIncomeClassificationRequest = false;
                pendingSettingsRevision = null;
            }

            if (purchases.Changed)
                await SendAsync(BudgetEvents.Purchase, purchases, stoppingToken);
            if (incomes.Changed)
                await SendAsync(BudgetEvents.Income, incomes, stoppingToken);
            if (users)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.Users), stoppingToken);
            if (categories)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.Categories), stoppingToken);
            if (recurringPurchases)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.RecurringPurchases), stoppingToken);
            if (purchaseClassification)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.PurchaseClassification), stoppingToken);
            if (purchaseClassificationRequest)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.PurchaseClassificationRequested), stoppingToken);
            if (incomeClassification)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.IncomeClassification), stoppingToken);
            if (incomeClassificationRequest)
                await SendToActiveKeysAsync(ServerSynchronization.Changed,
                    new ServerSynchronizationDto(ServerSynchronization.IncomeClassificationRequested), stoppingToken);
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
            if (kind != BudgetEvents.Purchase)
                return await database.Incomes.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new BudgetOperationPreviewDto(x.UserId, x.User.Name, x.Source, x.Sum, x.Currency,
                        x.User.ImageRaw != null && x.User.ImageRaw.Length != 0)).SingleOrDefaultAsync(timeout.Token);

            var purchase = await database.Purchases.AsNoTracking().Where(row => row.Id == id)
                .Select(row => new
                {
                    row.UserId, Author = row.User.Name, row.Name,
                    HasAvatar = row.User.ImageRaw != null && row.User.ImageRaw.Length != 0,
                    Items = row.Items.Select(item => new PurchaseAmountDto(item.Sum, item.Currency)).ToList(),
                    row.Adjustment
                }).SingleOrDefaultAsync(timeout.Token);
            if (purchase is null)
                return null;

            if (purchase.Adjustment is { } adjustment && (adjustment.Amount != 0 || purchase.Items.Count == 0))
                purchase.Items.Add(new(adjustment.Amount, adjustment.Currency));
            var amounts = purchase.Items.GroupBy(line => line.Currency)
                .Select(group => new PurchaseAmountDto(group.Sum(line => line.Amount), group.Key)).ToArray();
            var amount = amounts.Length == 1 ? amounts[0] : null;
            return new(purchase.UserId, purchase.Author, purchase.Name, amount?.Amount ?? 0, amount?.Currency, purchase.HasAvatar)
            {
                AmountsByCurrency = amounts.Length > 1 ? amounts : null
            };
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not load a budget notification preview: {Type}", error.GetType().Name);
            return null;
        }
    }
}
