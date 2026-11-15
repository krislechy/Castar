using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private async Task<string?> SaveIncomesAsync(
        Message message, BudgetUser user, string[] lines, bool edited, CancellationToken cancellationToken)
    {
        var inputs = lines.Select(ParseIncome).ToArray();
        var existing = await incomesService.GetEntityByMessageId(user.TelegramId, message.MessageId, cancellationToken);
        if (!edited && existing.Count > 0)
            return null;

        if (edited && inputs.Where(input => input.Sum >= 0).SequenceEqual(
                existing.Select(income => (income.Source, income.Sum))))
            return null;

        var incomes = new List<Income>(inputs.Length);
        var result = new StringBuilder();
        for (var index = 0; index < inputs.Length; index++)
        {
            var (source, sum) = inputs[index];
            if (index >= existing.Count)
            {
                if (edited && sum < 0)
                    continue;

                incomes.Add(new Income { Source = source, Sum = sum });
                result.AppendLine($"{settings.TelegramBot.IncomeCreateEmoji} {user.Name} добавил(а) начисление: {sum.ToCurrency()}, {source}");
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
            incomes.Add(income);
            result.AppendLine($"{settings.TelegramBot.IncomeUpdateEmoji} {user.Name} обновил(а) начисление: {sum.ToCurrency()}, {source}");
        }

        foreach (var income in existing.Skip(inputs.Length))
            result.AppendLine(IncomeDeleted(user, income));

        await incomesService.SaveMessageIncomes(user.TelegramId, message.MessageId, incomes, cancellationToken);
        return result.ToString();
    }

    private string IncomeDeleted(BudgetUser user, Income income) =>
        $"{settings.TelegramBot.IncomeDeleteEmoji} {user.Name} удалил(а) начисление: {income.Sum.ToCurrency()}, {income.Source}";

    private static (string Source, decimal Sum) ParseIncome(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && parts[0].Equals("/income", StringComparison.OrdinalIgnoreCase)
            && parts[2].ToDecimal() is { } sum)
            return (parts[1], sum);

        throw new FormatException("Формат начисления: /income [источник] [сумма].");
    }
}
