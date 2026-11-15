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
    string? LegacyVerificationCode = null);

public sealed record ReceiptItem(string Name, decimal Quantity, decimal UnitPrice, decimal Total);
