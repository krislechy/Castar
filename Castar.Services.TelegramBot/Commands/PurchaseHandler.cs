using System.Text;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Telegram.Bot;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private const string PurchaseFormat = "Один товар на строку: [название] [сумма] [валюта по желанию].";

    private async Task<string?> SaveManualPurchaseAsync(
        ITelegramBotClient bot, Message message, BudgetUser user, string[] lines, bool edited,
        CancellationToken cancellationToken)
    {
        if (lines.Length == 0)
            throw new FormatException(PurchaseFormat);

        var existing = await purchasesService.GetManualPurchaseByMessageId(user.TelegramId, message.MessageId, cancellationToken);
        if (existing is not null && !edited)
            return null;
        var policy = settings.ManualCurrencies;
        var originalItems = existing?.Items.ToArray() ?? [];
        var inputs = lines.Select((line, index) =>
        {
            var (name, sum, currency) = ParseItem(line, policy);
            return (Name: name, Sum: sum, Currency: currency ??
                (index < originalItems.Length ? originalItems[index].Currency : policy.DefaultCurrency));
        }).ToArray();
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
            foreach (var (name, sum, currency) in inputs)
            {
                purchase.Items.Add(NewManualItem(name, sum, currency));
                created.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {FormatManualAmount(sum, currency)}");
            }

            await purchasesService.Add(purchase, user.TelegramId, cancellationToken);
            return created.ToString();
        }

        if (existing.Items.Count == inputs.Length && inputs.Any(item => item.Sum < 0))
        {
            var deletion = await purchasesService.DeletePurchaseAsync(existing.Id, existing.Revision, cancellationToken);
            if (deletion != PurchaseMutationResult.Applied)
            {
                await ReplyAsync(bot, message, deletion == PurchaseMutationResult.Conflict
                    ? "Покупка изменилась. Проверьте её в клиенте и повторите удаление."
                    : "Покупка уже удалена.", cancellationToken);
                return null;
            }

            var totals = string.Join(" · ", existing.GetAmountsByCurrency()
                .Select(total => FormatManualAmount(total.Amount, total.Currency)));
            return $"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) покупку [{existing.Name}] — {totals}";
        }

        var prices = inputs.Select(item => (item.Name,
            existing.CalculateManualItemAmounts(1, item.Sum, item.Currency).Price, item.Currency));
        if (prices.SequenceEqual(originalItems.Select(item => (item.Name, item.Price, item.Currency))))
            return null;

        var result = new StringBuilder();
        for (var index = 0; index < inputs.Length; index++)
        {
            var (name, sum, currency) = inputs[index];
            if (index < originalItems.Length)
            {
                var item = originalItems[index];
                item.Name = name;
                item.Price = sum;
                item.Sum = sum * (decimal)item.Quantity;
                item.Currency = currency;
                result.AppendLine($"{settings.TelegramBot.PurchaseUpdateEmoji} {user.Name} обновил(а) товар [{name}] — {FormatManualAmount(item.Sum, currency)}");
                continue;
            }

            existing.Items.Add(NewManualItem(name, sum, currency));
            result.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) товар [{name}] — {FormatManualAmount(sum, currency)}");
        }

        foreach (var item in originalItems.Skip(inputs.Length))
        {
            existing.Items.Remove(item);
            result.AppendLine($"{settings.TelegramBot.PurchaseDeleteEmoji} {user.Name} удалил(а) товар [{item.Name}] — {FormatManualAmount(item.Sum, item.Currency)}");
        }

        await purchasesService.Update(existing, cancellationToken);
        return result.ToString();
    }

    private static Item NewManualItem(string name, decimal sum, string? currency) =>
        new() { Name = name, Price = sum, Sum = sum, Quantity = 1, Currency = currency };

    private static (string Name, decimal Sum, string? Currency) ParseItem(string line, ManualCurrencySettings policy) =>
        ParseManualAmount(line, policy) ?? throw new FormatException(PurchaseFormat);
}
