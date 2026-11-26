using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record PurchaseClassificationWorkDto(Guid PurchaseId, long Revision, string Merchant,
    IReadOnlyList<PurchaseClassificationItemDto> Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PurchaseName { get; init; }
}

public sealed record PurchaseClassificationItemDto(Guid ItemId, string Name, bool HasHistory, Guid? CategoryId);

public sealed record PurchaseClassificationWriteDto(IReadOnlyList<PurchaseClassificationResultDto> Items);

public sealed record PurchaseClassificationResultDto(Guid ItemId, Guid? CategoryId);

public sealed record PurchaseClassificationQueueDto(int QueuedCount, int PendingCount);

public sealed record ClassificationCountDto(int Count);

public sealed record IncomeClassificationWorkDto(Guid IncomeId, long Revision, string Source,
    bool HasHistory, Guid? CategoryId);
