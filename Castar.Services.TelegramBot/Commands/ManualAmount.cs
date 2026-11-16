using Castar.Domain.Models.SettingsModel;
using Castar.Extensions;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private static string FormatManualAmount(decimal amount, string? currency) => currency is null
        ? amount.ToCurrency()
        : $"{amount:0.00##########################} {currency}";

    private static (string Description, decimal Sum, string? Currency)? ParseManualAmount(
        string line, ManualCurrencySettings policy)
    {
        var splitAt = line.LastIndexOfAny([' ', '\t']);
        if (splitAt <= 0)
            return null;

        var last = line[(splitAt + 1)..];
        var beforeLast = line[..splitAt].TrimEnd();
        if (last.ToDecimal() is { } sum)
            return (beforeLast, sum, null);

        splitAt = beforeLast.LastIndexOfAny([' ', '\t']);
        if (splitAt <= 0 || beforeLast[(splitAt + 1)..].ToDecimal() is not { } amount)
            return null;

        var currency = ManualCurrencySettings.NormalizeCode(last);
        if (currency is null || !policy.Currencies.Contains(currency))
            throw new FormatException($"Неверный код валюты. Доступные валюты: {string.Join(", ", policy.Currencies)}.");

        return (beforeLast[..splitAt].TrimEnd(), amount, currency);
    }
}
