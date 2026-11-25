using Castar.Currency;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Castar.Compontents;

internal static class SelectionAmount
{
    internal static void EnableSelectAll(ListView list)
    {
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll,
            (sender, e) =>
            {
                ((ListView)sender).SelectAll();
                e.Handled = true;
            },
            (sender, e) =>
            {
                e.CanExecute = ((ListView)sender).HasItems;
                e.Handled = true;
            }));
        list.InputBindings.Add(new KeyBinding(ApplicationCommands.SelectAll, Key.A, ModifierKeys.Control));
    }

    internal static void Show(FrameworkElement? panel, TextBlock? text,
        IEnumerable<(decimal? Amount, string? Currency)> selection)
    {
        if (panel is null || text is null)
            return;

        var rows = selection.ToArray();
        if (rows.Length == 0)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }

        var known = rows.Where(row => row.Amount.HasValue && HistoricalExchangeRates.IsCurrencyCode(row.Currency))
            .Select(row => (row.Amount!.Value, row.Currency)).ToArray();
        var amount = known.Length == 0 ? "—" : MoneyText.FormatTotals(known);
        var excluded = rows.Length - known.Length;
        text.Text = excluded == 0 ? $"Выбрано: {amount}" : $"Выбрано: {amount} · вне суммы: {excluded}";
        panel.ToolTip = excluded == 0 ? null : "В сумму не вошли строки без валюты или исторического курса.";
        panel.Visibility = Visibility.Visible;
    }
}
