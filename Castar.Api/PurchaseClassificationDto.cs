namespace Castar.Api;

public sealed record PurchaseClassificationWorkDto(Guid PurchaseId, long Revision, string Merchant,
    IReadOnlyList<PurchaseClassificationItemDto> Items);

public sealed record PurchaseClassificationItemDto(Guid ItemId, string Name, bool HasHistory, Guid? CategoryId);

public sealed record PurchaseClassificationWriteDto(IReadOnlyList<PurchaseClassificationResultDto> Items);

public sealed record PurchaseClassificationResultDto(Guid ItemId, Guid? CategoryId);

public sealed record PurchaseClassificationQueueDto(int QueuedCount, int PendingCount);
