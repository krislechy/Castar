using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Services.Receipts;

namespace Castar.Services.TelegramBot;

public static class ReceiptPurchase
{
    public static Purchase Create(Receipt receipt, string source, int messageId) => new()
    {
        MessageId = messageId,
        Name = receipt.Name,
        RetailPlace = receipt.RetailPlace,
        RetailPlaceAddress = receipt.RetailPlaceAddress,
        AddedOn = receipt.IssuedAt.UtcDateTime,
        ReceiptDate = receipt.OperationDate,
        Currency = receipt.Currency,
        TotalSum = receipt.Total,
        RawData = receipt.LegacyVerificationCode is { } legacyCode ? Encoding.UTF8.GetBytes(legacyCode) : null,
        ReceiptSource = source,
        Items = new(receipt.Items.Select(item => new Item
        {
            Name = item.Name,
            Price = item.UnitPrice,
            Quantity = (double)item.Quantity,
            Sum = item.Total
        }))
    };
}
