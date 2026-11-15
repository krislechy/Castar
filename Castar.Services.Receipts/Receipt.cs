namespace Castar.Services.Receipts;

public sealed record Receipt(
    string Id,
    string Name,
    string? RetailPlace,
    string? RetailPlaceAddress,
    DateTimeOffset IssuedAt,
    string Currency,
    decimal Total,
    IReadOnlyList<ReceiptItem> Items,
    string? LegacyVerificationCode = null)
{
    public DateOnly OperationDate { get; init; } = DateOnly.FromDateTime(IssuedAt.Date);
}

public sealed record ReceiptItem(string Name, decimal Quantity, decimal UnitPrice, decimal Total);
