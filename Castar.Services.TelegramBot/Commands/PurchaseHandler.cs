using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private async Task<string?> SaveManualPurchaseAsync(
        Message message, BudgetUser user, string[] lines, bool edited, CancellationToken cancellationToken)
    {
        if (lines.Length == 0)
            throw new FormatException("Формат покупки: [наименование товара] [сумма].");

        var inputs = lines.Select(ParseItem).ToArray();
        var existing = await purchasesService.GetManualPurchaseByMessageId(user.TelegramId, message.MessageId, cancellationToken);
        if (existing is null)
        {
            if (edited && inputs.Any(item => item.Sum < 0))
                return null;

            var purchase = new Purchase
            {
                MessageId = message.MessageId,
                Name = inputs.Length == 1 ? inputs[0].Name : $"№{message.MessageId}"
            };
            var created = new StringBuilder();
            foreach (var (name, sum) in inputs)
            {
                purchase.Items.Add(NewManualItem(name, sum));
                created.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {sum.ToCurrency()}");
            }

            await purchasesService.Add(purchase, user.TelegramId, cancellationToken);
            return created.ToString();
        }

        if (!edited)
            return null;

        if (inputs.SequenceEqual(existing.Items.Select(item => (item.Name, item.Price))))
            return null;

        if (existing.Items.Count == inputs.Length && inputs.Any(item => item.Sum < 0))
        {
            await purchasesService.Delete(existing.Id, cancellationToken);
            return $"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) покупку [{existing.Name}] — {existing.TotalSum.ToCurrency()}";
        }

        var originalItems = existing.Items.ToArray();
        var result = new StringBuilder();
        for (var index = 0; index < inputs.Length; index++)
        {
            var (name, sum) = inputs[index];
            if (index < originalItems.Length)
            {
                var item = originalItems[index];
                item.Name = name;
                item.Price = sum;
                item.Sum = sum * (decimal)item.Quantity;
                result.AppendLine($"{settings.TelegramBot.PurchaseUpdateEmoji} {user.Name} обновил(а) товар [{name}] — {item.Sum.ToCurrency()}");
                continue;
            }

            existing.Items.Add(NewManualItem(name, sum));
            result.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {sum.ToCurrency()}");
        }

        foreach (var item in originalItems.Skip(inputs.Length))
        {
            existing.Items.Remove(item);
            result.AppendLine($"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) товар [{item.Name}] — {item.Sum.ToCurrency()}");
        }

        await purchasesService.Update(existing, cancellationToken);
        return result.ToString();
    }

    private static Item NewManualItem(string name, decimal sum) =>
        new() { Name = name, Price = sum, Sum = sum, Quantity = 1 };

    private static (string Name, decimal Sum) ParseItem(string line)
    {
        var splitAt = line.LastIndexOfAny([' ', '\t']);
        if (splitAt > 0 && line[(splitAt + 1)..].ToDecimal() is { } sum)
            return (line[..splitAt].TrimEnd(), sum);

        throw new FormatException("Формат покупки: [наименование товара] [сумма].");
    }
}
