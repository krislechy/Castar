namespace Castar.Api;

public sealed record UserDto(Guid Id, long TelegramId, string Name, int Sex, byte[] ColorRaw, byte[]? ImageRaw)
{
    public bool? TelegramNotificationsEnabled { get; init; }
}

public sealed record UserWriteDto(long TelegramId, string Name, int Sex, byte[] ColorRaw, byte[]? ImageRaw)
{
    public bool? TelegramNotificationsEnabled { get; init; }
}

public sealed record IncomeDto(Guid Id, Guid UserId, UserDto User, long MessageId, decimal Sum, string Source, DateTime AddedOn)
{
    public string? Currency { get; init; }
}

public sealed record IncomeWriteDto(Guid UserId, decimal Sum, string Source, DateTime AddedOn)
{
    public string? Currency { get; init; }
}

public sealed record PurchaseDto(Guid Id, Guid UserId, string Name, decimal TotalSum, string? Currency,
    long MessageId, DateTime? AddedOn, string? RetailPlace, string? RetailPlaceAddress,
    IReadOnlyList<PurchaseItemDto> Items)
{
    public DateOnly? ReceiptDate { get; init; }
    public long? Revision { get; init; }
    public bool HasReceiptSource { get; init; }
}

public sealed record PurchaseItemDto(Guid Id, string Name, int? Category, double Quantity, decimal Price, decimal Sum);

public sealed record PurchaseWriteDto(string Name, string? RetailPlace, string? RetailPlaceAddress, DateTime AddedOn);

public sealed record PurchaseItemWriteDto(string Name, double Quantity, decimal Price);

public sealed record PurchaseReceiptSourceDto(string? Content, bool IsLegacy);
