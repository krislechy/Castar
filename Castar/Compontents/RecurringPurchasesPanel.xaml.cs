using Castar.Api;
using Castar.Currency;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

internal enum RecurringPurchaseAction
{
    Add,
    Edit,
    Pause,
    Resume,
    Skip,
    Delete
}

public partial class RecurringPurchasesPanel : UserControl
{
    private readonly TimeZoneInfo displayZone;
    private bool isBusy;
    private bool hasLoaded;

    internal RecurringPurchasesPanel(TimeZoneInfo? displayZone = null)
    {
        this.displayZone = displayZone ?? TimeZoneInfo.Local;
        InitializeComponent();
        SelectionAmount.EnableSelectAll(Subscriptions);
    }

    internal event Action<RecurringPurchaseAction, RecurringPurchaseDto?>? ActionRequested;

    internal void SetRows(IReadOnlyList<RecurringPurchaseDto> subscriptions, string target,
        IReadOnlyDictionary<string, CurrencyRateDto> quotes)
    {
        var selected = Subscriptions.SelectedItems.OfType<SubscriptionRow>().Select(row => row.Subscription.Id).ToHashSet();
        var rows = subscriptions.OrderBy(item => item.IsPaused).ThenBy(item => item.NextRunUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Schedule.Template.Purchase.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(subscription => CreateRow(subscription, target, quotes)).ToArray();
        Subscriptions.ItemsSource = rows;
        foreach (var row in rows.Where(row => selected.Contains(row.Subscription.Id)))
            Subscriptions.SelectedItems.Add(row);
        hasLoaded = true;
        UpdateVisibility();
    }

    internal void SetBusy(bool busy)
    {
        isBusy = busy;
        AddSubscription.IsEnabled = !busy;
        Subscriptions.IsEnabled = !busy;
        Loading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisibility();
    }

    internal void SetError(string? error)
    {
        Error.Text = error ?? "";
        Error.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Subscriptions.Visibility = Subscriptions.HasItems ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = hasLoaded && !isBusy && Error.Visibility == Visibility.Collapsed && !Subscriptions.HasItems
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SubscriptionsSelection_Changed(object sender, SelectionChangedEventArgs e) =>
        SelectionAmount.Show(SelectionTotal, SelectionTotalText,
            Subscriptions.SelectedItems.OfType<SubscriptionRow>().SelectMany(row => row.SelectionAmounts));

    private SubscriptionRow CreateRow(RecurringPurchaseDto item, string target, IReadOnlyDictionary<string, CurrencyRateDto> quotes)
    {
        var template = item.Schedule.Template;
        var frequency = item.Schedule.Frequency switch
        {
            RecurringPurchaseFrequency.Weekly => "Еженедельно",
            RecurringPurchaseFrequency.Monthly => "Ежемесячно",
            RecurringPurchaseFrequency.Yearly => "Ежегодно",
            _ => ""
        };
        var zone = RecurringPurchaseTime.TryResolve(item.Schedule, out var savedZone)
            ? savedZone : displayZone;
        var hasNumericZone = item.Schedule.ZoneRuleId.HasValue || item.Schedule.UtcOffsetMinutes.HasValue;
        var timing = hasNumericZone
            ? $"{frequency} · {RecurringPurchaseTime.FormatOffset((int)zone.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes)}"
            : frequency;
        if (!item.IsPaused && item.NextRunUtc is { } next)
        {
            var local = TimeZoneInfo.ConvertTime(next, zone);
            var offset = hasNumericZone || zone.Id != displayZone.Id
                ? $" ({RecurringPurchaseTime.FormatOffset((int)local.Offset.TotalMinutes)})" : "";
            timing = $"{local:dd.MM.yyyy, HH:mm}{offset} · {frequency}";
        }
        try
        {
            var purchase = new Purchase { Currency = template.Purchase.Currency };
            var totals = template.Items.GroupBy(line => line.Currency ?? template.Purchase.Currency)
                .Select(group => (Amount: group.Sum(line =>
                    purchase.CalculateManualItemAmounts(line.Quantity, line.Price, line.Currency).Sum), Currency: group.Key))
                .ToArray();
            var row = new SubscriptionRow(item, template.Purchase.Name, timing,
                totals.Select(total => MoneyText.Format(total.Amount, total.Currency)).ToArray(),
                totals.Select(total => ((decimal?)total.Amount, total.Currency)).ToArray());
            if (target.Length == 0 || totals.All(total => total.Currency == target))
                return row;

            var foreign = totals.Where(total => total.Currency != target).ToArray();
            var missing = foreign.Where(total => total.Currency is null || !quotes.ContainsKey(total.Currency))
                .Select(total => total.Currency is { } currency ? $"{currency} → {target}" : "Сумма без исходной валюты").ToArray();
            if (missing.Length > 0)
                return row with { SelectionAmounts = [(null, target)], RateSummary = "Курс недоступен", RateDetails = string.Join("\n", missing) };

            var estimate = totals.Sum(total => total.Currency == target ? total.Amount
                : HistoricalExchangeRates.ConvertAmount(total.Amount, quotes[total.Currency!].Rate));
            var exchange = foreign.Select(total => quotes[total.Currency!]).ToArray();
            string RateLabel(CurrencyRateDto quote) => $"1 {quote.Currency} = {MoneyText.Format(quote.Rate, target)}";
            return row with
            {
                SelectionAmounts = [(estimate, target)],
                EstimatedAmount = $"≈ {MoneyText.Format(estimate, target)}",
                RateSummary = RateLabel(exchange[0]) + (exchange.Length > 1 ? $" · ещё {exchange.Length - 1}" : ""),
                RateDetails = string.Join("\n", exchange.Select(quote => $"{RateLabel(quote)} · {quote.QuotedOn:dd.MM.yyyy}"))
            };
        }
        catch (OverflowException)
        {
            return new(item, template.Purchase.Name, timing, ["—"], [(null, target)]);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Request(RecurringPurchaseAction.Add, null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubscriptionRow row })
            Request(RecurringPurchaseAction.Edit, row.Subscription);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: Button { DataContext: SubscriptionRow row } } menu)
            return;
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            child.Tag = row.Subscription;
            if (child.Name == "TogglePause")
                child.Header = row.Subscription.IsPaused ? "Возобновить" : "Поставить на паузу";
            else if (child.Name == "SkipNext")
                child.IsEnabled = !row.Subscription.IsPaused && row.Subscription.NextRunUtc.HasValue;
        }
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: RecurringPurchaseDto item })
            Request(item.IsPaused ? RecurringPurchaseAction.Resume : RecurringPurchaseAction.Pause, item);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: RecurringPurchaseDto item })
            Request(RecurringPurchaseAction.Skip, item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: RecurringPurchaseDto item })
            Request(RecurringPurchaseAction.Delete, item);
    }

    private void Request(RecurringPurchaseAction action, RecurringPurchaseDto? item)
    {
        if (!isBusy)
            ActionRequested?.Invoke(action, item);
    }

    private sealed record SubscriptionRow(RecurringPurchaseDto Subscription, string Name, string Timing, string[] Amounts,
        (decimal? Amount, string? Currency)[] SelectionAmounts)
    {
        public string? EstimatedAmount { get; init; }
        public string? RateSummary { get; init; }
        public string? RateDetails { get; init; }
    }
}
