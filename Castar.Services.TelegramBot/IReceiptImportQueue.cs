namespace Castar.Services.TelegramBot;

public sealed record ReceiptRetryRequest(string ProviderId, string Source, Guid UserId,
    long TelegramUserId, long ChatId, int MessageId);

public interface IReceiptImportQueue
{
    Task<bool> ContainsAsync(string providerId, string source, CancellationToken cancellationToken);
    Task<bool> EnqueueAsync(ReceiptRetryRequest request, DateTime? retryAfterUtc, CancellationToken cancellationToken);
    Task SetAcknowledgementAsync(string providerId, string source, int messageId, CancellationToken cancellationToken);
}
