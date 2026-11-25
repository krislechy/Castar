using Castar.Api;
using Castar.Currency;
using System.Globalization;
using System.Linq;

namespace Castar.Notifications;

internal static class BudgetNotificationText
{
    internal static string Format(string kind, int count, BudgetOperationPreviewDto? preview)
    {
        var summary = (kind, count) switch
        {
            (BudgetEvents.Purchase, 1) => "Новый расход",
            (BudgetEvents.Income, 1) => "Новый доход",
            (BudgetEvents.Purchase, _) => $"Новые расходы ({count})",
            _ => $"Новые доходы ({count})"
        };
        return preview is null ? summary : $"{summary}\n{Title(preview)}\n{Details(preview)}";
    }

    internal static string Title(BudgetOperationPreviewDto preview) => Limit(preview.Title, 90);

    internal static string Details(BudgetOperationPreviewDto preview)
    {
        var amount = preview.AmountsByCurrency is { Count: > 0 } amounts
            ? MoneyText.FormatTotals(amounts.Select(row => (row.Amount, row.Currency)))
            : MoneyText.Format(preview.Amount, preview.Currency);
        return $"{Limit(preview.Author, 56)} · {amount}";
    }

    private static string Limit(string value, int maxLength)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= maxLength)
            return value;
        var characters = StringInfo.ParseCombiningCharacters(value);
        var next = System.Array.FindIndex(characters, index => index >= maxLength);
        return next <= 0 ? "…" : value[..characters[next - 1]] + "…";
    }
}
