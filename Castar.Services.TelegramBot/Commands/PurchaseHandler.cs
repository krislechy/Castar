using System.Text;
using Castar.Domain.Models.DataBase;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private const string PurchaseFormat = "Формат покупки: [наименование товара] [сумма] [валюта по желанию].";

    private async Task<string?> SaveManualPurchaseAsync(
        Message message, BudgetUser user, string[] lines, bool edited, CancellationToken cancellationToken)
    {
        if (lines.Length == 0)
            throw new FormatException(PurchaseFormat);

        var inputs = lines.Select(ParseItem).ToArray();
        var currencies = inputs.Select(item => item.Currency).OfType<string>().Distinct().Take(2).ToArray();
        if (currencies.Length > 1)
            throw new FormatException("Для позиций одной покупки укажите одну валюту.");

        var existing = await purchasesService.GetManualPurchaseByMessageId(user.TelegramId, message.MessageId, cancellationToken);
        var currency = currencies.FirstOrDefault() ?? existing?.Currency;
        if (existing is null)
        {
            if (edited && inputs.Any(item => item.Sum < 0))
                return null;

            var purchase = new Purchase
            {
                MessageId = message.MessageId,
                Name = inputs.Length == 1 ? inputs[0].Name : $"№{message.MessageId}",
                Currency = currency
            };
            var created = new StringBuilder();
            foreach (var (name, sum, _) in inputs)
            {
                purchase.Items.Add(NewManualItem(name, sum));
                created.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {FormatManualAmount(sum, currency)}");
            }

            await purchasesService.Add(purchase, user.TelegramId, cancellationToken);
            return created.ToString();
        }

        if (!edited)
            return null;

        if (currency == existing.Currency && inputs.Select(item => (item.Name, item.Sum))
                .SequenceEqual(existing.Items.Select(item => (item.Name, item.Price))))
            return null;

        if (existing.Items.Count == inputs.Length && inputs.Any(item => item.Sum < 0))
        {
            await purchasesService.Delete(existing.Id, cancellationToken);
            return $"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) покупку [{existing.Name}] — {FormatManualAmount(existing.TotalSum, existing.Currency)}";
        }

        var originalItems = existing.Items.ToArray();
        var result = new StringBuilder();
        existing.Currency = currency;
        for (var index = 0; index < inputs.Length; index++)
        {
            var (name, sum, _) = inputs[index];
            if (index < originalItems.Length)
            {
                var item = originalItems[index];
                item.Name = name;
                item.Price = sum;
                item.Sum = sum * (decimal)item.Quantity;
                result.AppendLine($"{settings.TelegramBot.PurchaseUpdateEmoji} {user.Name} обновил(а) товар [{name}] — {FormatManualAmount(item.Sum, currency)}");
                continue;
            }

            existing.Items.Add(NewManualItem(name, sum));
            result.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {FormatManualAmount(sum, currency)}");
        }

        foreach (var item in originalItems.Skip(inputs.Length))
        {
            existing.Items.Remove(item);
            result.AppendLine($"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) товар [{item.Name}] — {FormatManualAmount(item.Sum, currency)}");
        }

        await purchasesService.Update(existing, cancellationToken);
        return result.ToString();
    }

    private static Item NewManualItem(string name, decimal sum) =>
        new() { Name = name, Price = sum, Sum = sum, Quantity = 1 };

    private static (string Name, decimal Sum, string? Currency) ParseItem(string line) =>
        ParseManualAmount(line) ?? throw new FormatException(PurchaseFormat);
}
