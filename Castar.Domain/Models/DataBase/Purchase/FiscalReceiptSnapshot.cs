using System.Text.Json;

namespace Castar.Domain.Models.DataBase;

public sealed record FiscalReceiptSnapshot(string Name, string? RetailPlace, string? RetailPlaceAddress,
    DateTimeOffset? AddedOn, DateOnly? ReceiptDate, string? Currency, decimal TotalSum,
    FiscalReceiptLine[] Items, Guid? UserId = null, bool StoredInUtc = false)
{
    public static string Capture(Purchase purchase) => JsonSerializer.Serialize(new FiscalReceiptSnapshot(
        purchase.Name, purchase.RetailPlace, purchase.RetailPlaceAddress,
        purchase.AddedOn is { } date ? new DateTimeOffset(BudgetTime.AsUtc(date)) : null,
        purchase.ReceiptDate, purchase.Currency, purchase.TotalSum,
        purchase.Items.Select(FiscalReceiptLine.Capture).ToArray(), purchase.UserId, StoredInUtc: true));

    public static FiscalReceiptSnapshot? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<FiscalReceiptSnapshot>(json) is { Items: not null } snapshot
                ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool HeaderChanged(Purchase purchase)
    {
        if (!StoredInUtc && AddedOn is { } legacy && BudgetTime.Belgrade.IsInvalidTime(legacy.DateTime))
            return true;
        var originalDate = AddedOn is not { } saved ? (DateTime?)null
            : StoredInUtc ? saved.UtcDateTime : BudgetTime.FromLocal(saved.DateTime, BudgetTime.Belgrade);
        return Name != purchase.Name || RetailPlace != purchase.RetailPlace || RetailPlaceAddress != purchase.RetailPlaceAddress ||
            originalDate != purchase.AddedOn || ReceiptDate != purchase.ReceiptDate || Currency != purchase.Currency ||
            TotalSum != purchase.TotalSum || ((UserId ?? purchase.TelegramOriginUserId) is { } owner && owner != purchase.UserId);
    }
}

public sealed record FiscalReceiptLine(Guid Id, string Name, double Quantity, decimal Price, decimal Sum)
{
    public static FiscalReceiptLine Capture(Item item) => new(item.Id, item.Name, item.Quantity, item.Price, item.Sum);
}
