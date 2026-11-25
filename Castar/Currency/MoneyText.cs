using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Castar.Currency;

internal static class MoneyText
{
    internal static string Format(decimal amount, string? currency)
    {
        var number = amount.ToString("#,0.00##########################", CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(currency) ? number : $"{number} {currency}";
    }

    internal static string FormatRounded(decimal amount, string? currency) =>
        Format(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), currency);

    internal static IEnumerable<string> FormatGroups(IEnumerable<(decimal Amount, string? Currency)> amounts) =>
        amounts.GroupBy(amount => amount.Currency)
            .Select(group => Format(group.Sum(amount => amount.Amount), group.Key));

    internal static string FormatTotals(IEnumerable<(decimal Amount, string? Currency)> amounts) =>
        string.Join(" · ", FormatGroups(amounts));
}
