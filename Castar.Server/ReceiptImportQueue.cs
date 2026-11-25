using System.Security.Cryptography;
using System.Text;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal sealed class ReceiptImportQueue(IServiceScopeFactory scopes, SettingsModel settings,
    ServerSettings serverSettings, IEnumerable<IReceiptProvider> providers, ReceiptQueueNotifications notifications,
    TimeProvider clock, ILogger<ReceiptImportQueue> logger, Func<bool> activationPending)
    : BackgroundService, IReceiptImportQueue
{
    private readonly SemaphoreSlim changed = new(0, 1);
    private long settingsRevision;
    private (long Revision, int RetryMinutes)? reconciledSchedule;
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<bool> ContainsAsync(string providerId, string source, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var key = GetKey(providerId, source);
        return await db.QueuedReceipts.AnyAsync(receipt => receipt.Key == key, cancellationToken);
    }

    public async Task<bool> EnqueueAsync(ReceiptRetryRequest request, DateTime? retryAfterUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var receipt = new QueuedReceipt
        {
            Key = GetKey(request.ProviderId, request.Source),
            ProviderId = request.ProviderId,
            Source = request.Source,
            UserId = request.UserId,
            TelegramUserId = request.TelegramUserId,
            ChatId = request.ChatId,
            MessageId = request.MessageId
        };
        var revision = Interlocked.Read(ref settingsRevision);
        var retryMinutes = settings.Receipts.RetryMinutes;
        Schedule(receipt, UtcNow, retryAfterUtc, retryMinutes);
        db.QueuedReceipts.Add(receipt);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 })
        {
            return false;
        }

        // Settings can change before this insert commits, after the worker has already rescheduled existing rows.
        if (revision != Interlocked.Read(ref settingsRevision) || retryMinutes != settings.Receipts.RetryMinutes)
            Interlocked.Increment(ref settingsRevision);
        Wake();
        return true;
    }

    public async Task SetAcknowledgementAsync(string providerId, string source, int messageId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var key = GetKey(providerId, source);
        await db.QueuedReceipts.Where(receipt => receipt.Key == key)
            .ExecuteUpdateAsync(receipts => receipts.SetProperty(receipt => receipt.AcknowledgementMessageId, messageId),
                cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serverSettings.Changed += OnSettingsChanged;
        try
        {
            while (activationPending())
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime? next;
                try
                {
                    next = await RunNextAsync(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Сбой обработки очереди чеков ({ErrorType}).", error.GetType().Name);
                    next = UtcNow.AddMinutes(1);
                }

                if (next is null)
                    await changed.WaitAsync(stoppingToken);
                else if (next > UtcNow)
                {
                    var delay = next.Value - UtcNow;
                    if (delay > TimeSpan.Zero)
                        await changed.WaitAsync(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1), stoppingToken);
                }
            }
        }
        finally
        {
            serverSettings.Changed -= OnSettingsChanged;
        }
    }

    private async Task<DateTime?> RunNextAsync(CancellationToken cancellationToken)
    {
        var revision = Interlocked.Read(ref settingsRevision);
        var receiptSettings = settings.Receipts;
        if (!settings.Systems.IsEnableTelegramBot || string.IsNullOrWhiteSpace(settings.TelegramBot.TelegramBotApi_KEY))
            return null;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        var schedule = (revision, receiptSettings.RetryMinutes);
        if (receiptSettings.IsEnabled && reconciledSchedule != schedule)
        {
            var reschedule = await db.QueuedReceipts
                .Where(receipt => receipt.Result == null && receipt.ScheduledRetryMinutes != receiptSettings.RetryMinutes)
                .ToListAsync(cancellationToken);
            foreach (var pending in reschedule)
                Schedule(pending, pending.LastAttemptUtc, pending.RetryAfterUtc, receiptSettings.RetryMinutes);
            if (reschedule.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
            reconciledSchedule = schedule;
        }

        var receipt = await db.QueuedReceipts
            .Where(candidate => receiptSettings.IsEnabled || candidate.Result != null)
            .OrderBy(candidate => candidate.NextAttemptUtc).ThenBy(candidate => candidate.Key)
            .FirstOrDefaultAsync(cancellationToken);
        var now = UtcNow;
        if (receipt is null || receipt.NextAttemptUtc > now)
            return receipt?.NextAttemptUtc;

        if (receipt.Result is null)
            await ProcessAsync(scope.ServiceProvider, db, receipt, cancellationToken);

        if (receipt.Result is not null)
        {
            var delay = await notifications.DeliverAsync(receipt, cancellationToken);
            if (delay is { } retry)
                receipt.NextAttemptUtc = UtcNow.Add(retry);
            else
                db.QueuedReceipts.Remove(receipt);
            await db.SaveChangesAsync(cancellationToken);
        }

        return UtcNow;
    }

    private async Task ProcessAsync(IServiceProvider services, CastarDbContext db,
        QueuedReceipt receipt, CancellationToken cancellationToken)
    {
        var provider = providers.SingleOrDefault(candidate => candidate.Id == receipt.ProviderId);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            user => user.Id == receipt.UserId && user.TelegramId == receipt.TelegramUserId, cancellationToken);
        if (owner is null || provider is null)
        {
            receipt.Result = owner is null ? "Чек не добавлен: пользователь больше не зарегистрирован."
                : "Чек не добавлен: поставщик больше не доступен в Castar.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        Receipt data;
        try
        {
            data = await provider.GetReceiptAsync(receipt.Source, cancellationToken);
        }
        catch (Exception error) when (ReceiptProviderFailure.IsTemporary(error, cancellationToken))
        {
            Schedule(receipt, UtcNow, (error as ReceiptProviderUnavailableException)?.RetryAfterUtc,
                settings.Receipts.RetryMinutes);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (Exception error) when (ReceiptProviderFailure.GetPermanentMessage(error) is { } message)
        {
            receipt.Result = $"Чек не добавлен: {message}";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var purchase = ReceiptPurchase.Create(data, receipt.Source, receipt.MessageId);
        purchase.UserId = owner.Id;
        receipt.Result = $"Чек добавлен. {data.Name} — {data.Total:F2} {data.Currency}";
        var repository = services.GetRequiredService<IPurchasesRepository>();
        // The repository's SaveChanges commits the purchase and its result together.
        if (await repository.AddReceipt(purchase, provider.Id, data.Id, cancellationToken) is null)
        {
            receipt.Result = "Этот чек уже добавлен.";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void Schedule(QueuedReceipt receipt, DateTime attemptedUtc, DateTime? retryAfterUtc, int interval)
    {
        receipt.LastAttemptUtc = attemptedUtc;
        receipt.ScheduledRetryMinutes = interval;
        receipt.RetryAfterUtc = retryAfterUtc;
        var configured = attemptedUtc.AddMinutes(interval);
        receipt.NextAttemptUtc = retryAfterUtc is { } limit && limit > configured ? limit : configured;
    }

    private static string GetKey(string providerId, string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(providerId, "\0", source.Trim()))));

    private void OnSettingsChanged()
    {
        Interlocked.Increment(ref settingsRevision);
        Wake();
    }

    private void Wake()
    {
        try
        {
            changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake already covers the change.
        }
    }
}
