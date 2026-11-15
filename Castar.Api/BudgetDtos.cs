namespace Castar.Api;

public sealed record UserDto(Guid Id, long TelegramId, string Name, int Sex, byte[] ColorRaw, byte[]? ImageRaw);

public sealed record UserWriteDto(long TelegramId, string Name, int Sex, byte[] ColorRaw, byte[]? ImageRaw);

public sealed record IncomeDto(Guid Id, Guid UserId, UserDto User, long MessageId, decimal Sum, string Source, DateTime AddedOn);

public sealed record IncomeWriteDto(Guid UserId, decimal Sum, string Source, DateTime AddedOn);

public sealed record PurchaseDto(Guid Id, Guid UserId, string Name, decimal TotalSum, string? Currency,
    long MessageId, DateTime? AddedOn, string? RetailPlace, string? RetailPlaceAddress,
    IReadOnlyList<PurchaseItemDto> Items);

public sealed record PurchaseItemDto(Guid Id, string Name, int? Category, double Quantity, decimal Price, decimal Sum);
