namespace Castar.Domain.Models.DataBase;

public sealed class RecurringPurchase
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ScheduleJson { get; set; } = "";
    public bool IsPaused { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
    public int RetryCount { get; set; }
    public int NextOccurrence { get; set; }
    public long Revision { get; set; }
}
