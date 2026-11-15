using Castar.Extensions;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private static string FormatManualAmount(decimal amount, string? currency) => currency is null
        ? amount.ToCurrency()
        : $"{amount:0.00##########################} {currency}";

    private static (string Description, decimal Sum, string? Currency)? ParseManualAmount(string line)
    {
        var splitAt = line.LastIndexOfAny([' ', '\t']);
        if (splitAt <= 0)
            return null;

        string? currency = null;
        var last = line[(splitAt + 1)..];
        if (last.Length == 3 && last.All(char.IsAsciiLetter))
        {
            currency = last.ToUpperInvariant();
            line = line[..splitAt].TrimEnd();
            splitAt = line.LastIndexOfAny([' ', '\t']);
        }

        return splitAt > 0 && line[(splitAt + 1)..].ToDecimal() is { } sum
            ? (line[..splitAt].TrimEnd(), sum, currency)
            : null;
    }
}
