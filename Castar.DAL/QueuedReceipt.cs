namespace Castar.DAL;

public sealed class QueuedReceipt
{
    public string Key { get; init; } = null!;
    public string ProviderId { get; init; } = null!;
    public string Source { get; init; } = null!;
    public Guid UserId { get; init; }
    public long TelegramUserId { get; init; }
    public long ChatId { get; init; }
    public int MessageId { get; init; }
    public int? AcknowledgementMessageId { get; set; }
    public DateTime LastAttemptUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
    public int ScheduledRetryMinutes { get; set; }
    public string? Result { get; set; }
}
