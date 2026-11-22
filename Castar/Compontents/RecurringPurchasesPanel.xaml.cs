using Castar.Api;
using Castar.Currency;
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
    }

    internal event Action<RecurringPurchaseAction, RecurringPurchaseDto?>? ActionRequested;

    internal void SetRows(IReadOnlyList<RecurringPurchaseDto> subscriptions)
    {
        var selected = (Subscriptions.SelectedItem as SubscriptionRow)?.Subscription.Id;
        var rows = subscriptions.OrderBy(item => item.IsPaused).ThenBy(item => item.NextRunUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Schedule.Template.Purchase.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreateRow).ToArray();
        Subscriptions.ItemsSource = rows;
        if (selected.HasValue)
            Subscriptions.SelectedItem = rows.FirstOrDefault(row => row.Subscription.Id == selected);
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

    private SubscriptionRow CreateRow(RecurringPurchaseDto item)
    {
        var template = item.Schedule.Template;
        var frequency = item.Schedule.Frequency switch
        {
            RecurringPurchaseFrequency.Weekly => "Еженедельно",
            RecurringPurchaseFrequency.Monthly => "Ежемесячно",
            RecurringPurchaseFrequency.Yearly => "Ежегодно",
            _ => ""
        };
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(item.Schedule.TimeZoneId, out var savedZone)
            ? savedZone : displayZone;
        var timing = frequency;
        if (!item.IsPaused && item.NextRunUtc is { } next)
        {
            var local = TimeZoneInfo.ConvertTime(next, zone);
            var offset = zone.Id == displayZone.Id ? "" : $" (UTC{local:zzz})";
            timing = $"{local:dd.MM.yyyy, HH:mm}{offset} · {frequency}";
        }
        return new(item, template.Purchase.Name, timing, FormatAmount(template));
    }

    private static string FormatAmount(PurchaseCreateDto template)
    {
        try
        {
            return HistoricalExchangeRates.Format(template.Items.Sum(item => (decimal)item.Quantity * item.Price),
                template.Purchase.Currency);
        }
        catch (OverflowException)
        {
            return "—";
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

    private sealed record SubscriptionRow(RecurringPurchaseDto Subscription, string Name, string Timing, string Amount);
}
