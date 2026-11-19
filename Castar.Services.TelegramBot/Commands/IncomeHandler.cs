using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private async Task<string?> SaveIncomesAsync(
        Message message, BudgetUser user, string[] lines, bool edited, CancellationToken cancellationToken)
    {
        var existing = await incomesService.GetEntityByMessageId(user.TelegramId, message.MessageId, cancellationToken);
        if (!edited && existing.Count > 0)
            return null;

        var policy = settings.ManualCurrencies;
        var inputs = lines.Select(line => ParseIncome(line, policy)).ToArray();
        if (edited)
        {
            var retained = inputs.Where(input => input.Sum >= 0).ToArray();
            if (retained.Select(input => input.Source).SequenceEqual(existing.Select(income => income.Source)))
            {
                if (retained.Zip(existing).All(pair => pair.First.Sum == pair.Second.Sum
                        && (pair.First.Currency is null || pair.First.Currency == pair.Second.Currency)))
                    return null;

                // After deleting a row, its negative marker can remain in the edited message.
                inputs = retained;
            }
        }

        var incomes = new List<Income>(inputs.Length);
        var result = new StringBuilder();
        for (var index = 0; index < inputs.Length; index++)
        {
            var (source, sum, currency) = inputs[index];
            if (index >= existing.Count)
            {
                if (edited && sum < 0)
                    continue;

                currency ??= policy.DefaultCurrency;
                incomes.Add(new Income { Source = source, Sum = sum, Currency = currency });
                result.AppendLine($"{settings.TelegramBot.IncomeCreateEmoji} {user.Name} добавил(а) начисление: {FormatManualAmount(sum, currency)}, {source}");
                continue;
            }

            var income = existing[index];
            if (sum < 0)
            {
                result.AppendLine(IncomeDeleted(user, income));
                continue;
            }

            income.Source = source;
            income.Sum = sum;
            income.Currency = currency ?? income.Currency;
            incomes.Add(income);
            result.AppendLine($"{settings.TelegramBot.IncomeUpdateEmoji} {user.Name} обновил(а) начисление: {FormatManualAmount(sum, income.Currency)}, {source}");
        }

        foreach (var income in existing.Skip(inputs.Length))
            result.AppendLine(IncomeDeleted(user, income));

        await incomesService.SaveMessageIncomes(user.TelegramId, message.MessageId, incomes, cancellationToken);
        return result.ToString();
    }

    private string IncomeDeleted(BudgetUser user, Income income) =>
        $"{settings.TelegramBot.IncomeDeleteEmoji} {user.Name} удалил(а) начисление: {FormatManualAmount(income.Sum, income.Currency)}, {income.Source}";

    private static (string Source, decimal Sum, string? Currency) ParseIncome(string line, ManualCurrencySettings policy)
    {
        if (ParseManualAmount(line, policy) is { } amount)
        {
            var command = BotMenu.FirstWord(amount.Description);
            if (command.Equals("/" + BotMenu.Income, StringComparison.OrdinalIgnoreCase))
            {
                var source = amount.Description[command.Length..].Trim();
                if (source.Length > 0)
                    return (source, amount.Sum, amount.Currency);
            }
        }

        throw new FormatException("Формат начисления: /income [источник] [сумма] [валюта по желанию].");
    }
}
