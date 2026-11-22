using System.Text.Json.Serialization;

namespace Castar.Api;

public enum RecurringPurchaseFrequency
{
    Weekly,
    Monthly,
    Yearly
}

public sealed record RecurringPurchaseWriteDto(PurchaseCreateDto Template,
    RecurringPurchaseFrequency Frequency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZoneId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UtcOffsetMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZoneRuleId = null);

public sealed record RecurringPurchaseDto(Guid Id, RecurringPurchaseWriteDto Schedule,
    DateTimeOffset? NextRunUtc, bool IsPaused, long Revision)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RetryAfterUtc { get; init; }
}
