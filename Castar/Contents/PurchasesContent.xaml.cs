using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Castar.Contents;

public partial class PurchasesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromSeconds(15) };
    private PurchaseRow[] purchases = [];
    private CancellationTokenSource? activeRequest;
    private bool hasLoaded;
    private bool refreshPending;

    public PurchasesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        InitializeComponent();
        server.ConnectionChanged += Server_ConnectionChanged;
        settings.PropertyChanged += Settings_PropertyChanged;
        polling.Tick += async (_, _) =>
        {
            if (activeRequest is null)
                await RefreshAsync();
        };
    }

    private async void Purchases_Loaded(object sender, RoutedEventArgs e) => await StartPollingAsync();

    private void Purchases_Unloaded(object sender, RoutedEventArgs e) => StopPolling();

    private async void Purchases_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
            await StartPollingAsync();
        else
            StopPolling();
    }

    private async Task StartPollingAsync()
    {
        if (!IsVisible || polling.IsEnabled)
            return;

        polling.Start();
        await RefreshAsync();
    }

    private void StopPolling()
    {
        polling.Stop();
        CancelRefresh();
    }

    private async void Dates_DateRangeChanged(object sender, EventArgs e)
    {
        if (!polling.IsEnabled)
            return;

        ClearPurchases();
        await RefreshAsync();
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        CancelRefresh();
        ClearPurchases();
        if (polling.IsEnabled)
            await RefreshAsync();
    }

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientSettings.DisplayCurrency) && polling.IsEnabled)
            await RefreshAsync();
    }

    private void ClearPurchases()
    {
        hasLoaded = false;
        ShowPurchases([], "");
        ShowStatus("");
        Progress.IsLoading = false;
    }

    private void CancelRefresh()
    {
        refreshPending = false;
        var previous = activeRequest;
        activeRequest = null;
        previous?.Cancel();
    }

    internal Task RefreshAfterServerChangeAsync()
    {
        if (!polling.IsEnabled)
            return Task.CompletedTask;
        if (activeRequest is null)
            return RefreshAsync();

        refreshPending = true;
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        CancelRefresh();
        var range = Dates.RangeDate;
        if (range.StartDate > range.EndDate)
        {
            ShowStatus("Дата начала позже даты окончания.");
            Progress.IsLoading = false;
            return;
        }

        using var request = new CancellationTokenSource();
        activeRequest = request;
        Progress.IsLoading = !hasLoaded;
        try
        {
            var rows = await server.GetPurchasesAsync(range.StartDate, range.EndDate, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            rows = rows.OrderByDescending(purchase => purchase.AddedOn).ThenBy(purchase => purchase.Id).ToArray();
            var target = settings.DisplayCurrency;
            var dated = rows.Select(row => (row.Currency, Date: OperationDate(row))).Where(row => row.Date.HasValue);
            var rates = await exchangeRates.GetAsync(dated.Select(row => (row.Currency, row.Date!.Value)), target, request.Token);
            if (request != activeRequest || refreshPending)
                return;
            var views = rows.Select(row => new PurchaseRow(row, target, RateFor(row, target, rates))).ToArray();
            ShowPurchases(views, target);
            hasLoaded = true;
            ShowStatus(target.Length == 0
                ? views.Any(row => !HistoricalExchangeRates.IsCurrencyCode(row.Source.Currency)) ? "Покупки без валюты исключены из итога." : ""
                : views.Any(row => row.Rate is null) ? "Покупки без валюты или курса исключены из итога." : "");
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == activeRequest && !refreshPending)
                ShowStatus(CastarServerClient.DescribeError(error));
        }
        finally
        {
            if (request == activeRequest)
            {
                activeRequest = null;
                if (refreshPending)
                    _ = RefreshAsync();
                else
                    Progress.IsLoading = false;
            }
        }
    }

    private static DateOnly? OperationDate(PurchaseDto purchase) => purchase.ReceiptDate
        ?? (purchase.AddedOn is { } date ? DateOnly.FromDateTime(date) : null);

    private static decimal? RateFor(PurchaseDto row, string target,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        if (target.Length != 0 && target == row.Currency)
            return 1m;
        return row.Currency is { } currency && OperationDate(row) is { } date
            && rates.TryGetValue((currency, date), out var rate) ? rate : null;
    }

    private void ShowPurchases(PurchaseRow[] rows, string target)
    {
        var amounts = target.Length == 0
            ? rows.Where(row => HistoricalExchangeRates.IsCurrencyCode(row.Source.Currency)).Select(row => (row.Source.TotalSum, row.Source.Currency))
            : rows.Where(row => row.Rate.HasValue).Select(row => (row.DisplayTotal, (string?)target));
        ExpenseTotal.Text = HistoricalExchangeRates.FormatTotals(amounts);
        if (purchases.Length == rows.Length && purchases.Zip(rows).All(pair => pair.First.EquivalentTo(pair.Second)))
            return;

        var selectedId = (PurchasesList.SelectedItem as PurchaseRow)?.Id;
        purchases = rows;
        PurchasesList.ItemsSource = rows;
        PurchasesList.SelectedItem = rows.FirstOrDefault(purchase => purchase.Id == selectedId);
    }

    private void ShowStatus(string status)
    {
        StatusText.Text = status;
        StatusText.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyPurchases.Visibility = hasLoaded && purchases.Length == 0 && status.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PurchasesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailsPanel.DataContext = PurchasesList.SelectedItem;
    }

    private sealed class PurchaseRow(PurchaseDto source, string target, decimal? rate)
    {
        public PurchaseDto Source { get; } = source;
        public decimal? Rate { get; } = rate;
        private string Target { get; } = target;
        public Guid Id => Source.Id;
        public string Name => Source.Name;
        public DateTime? AddedOn => Source.AddedOn;
        public string? RetailPlace => Source.RetailPlace;
        public string? RetailPlaceAddress => Source.RetailPlaceAddress;
        public decimal DisplayTotal => Rate is { } value && Source.Currency != Target
            ? HistoricalExchangeRates.ConvertAmount(Source.TotalSum, value) : Source.TotalSum;
        public string Amount => HistoricalExchangeRates.Format(DisplayTotal, Rate.HasValue ? Target : Source.Currency);
        public string ReceiptTotal => FormatItemAmount(Source.TotalSum, Source.Currency, Target, Rate);

        public ItemRow[] Items { get; } = source.Items.Select(item => new ItemRow(item.Name, item.Quantity,
            FormatItemAmount(item.Price, source.Currency, target, rate),
            FormatItemAmount(item.Sum, source.Currency, target, rate))).ToArray();

        public bool EquivalentTo(PurchaseRow other) => Target == other.Target && Rate == other.Rate
            && (Source with { Items = other.Source.Items }) == other.Source
            && Source.Items.SequenceEqual(other.Source.Items);

        private static string FormatItemAmount(decimal amount, string? source, string target, decimal? rate)
        {
            var original = HistoricalExchangeRates.Format(amount, source);
            return rate is { } value && source != target
                ? $"{HistoricalExchangeRates.Format(HistoricalExchangeRates.ConvertAmount(amount, value), target)} ({original})"
                : original;
        }
    }

    private sealed record ItemRow(string Name, double Quantity, string Price, string Sum);
}
