using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record BudgetEventDto(string Kind, bool Created)
{
    public int CreatedCount { get; init; }
    public Guid? EntityId { get; init; }
    public DateTime? AddedOn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AddedOnUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AffectedFromUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BudgetOperationPreviewDto? Preview { get; init; }
}

public sealed record BudgetOperationPreviewDto(Guid UserId, string Author, string Title, decimal Amount,
    string? Currency, bool HasAvatar)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PurchaseAmountDto>? AmountsByCurrency { get; init; }
}

public static class BudgetEvents
{
    public const string Path = "api/events";
    public const string Method = "BudgetChanged";
    public const string Ready = "BudgetReady";
    public const string AffectedFromHeader = "X-Castar-Budget-Affected-From";
    public const string Purchase = "purchase";
    public const string Income = "income";
}
