using Castar.Api;
using Castar.Compontents;
using Castar.ServerConnection;
using System;
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
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromSeconds(15) };
    private PurchaseDto[] purchases = [];
    private CancellationTokenSource? activeRequest;
    private bool hasLoaded;
    private bool refreshPending;

    public PurchasesContent(CastarServerClient server)
    {
        this.server = server;
        InitializeComponent();
        server.ConnectionChanged += Server_ConnectionChanged;
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

    private void ClearPurchases()
    {
        hasLoaded = false;
        ShowPurchases([]);
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
            ShowPurchases(rows);
            hasLoaded = true;
            ShowStatus("");
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

    private void ShowPurchases(PurchaseDto[] rows)
    {
        if (purchases.Length == rows.Length && purchases.Zip(rows).All(pair =>
            (pair.First with { Items = pair.Second.Items }) == pair.Second &&
            pair.First.Items.SequenceEqual(pair.Second.Items)))
            return;

        var selectedId = (PurchasesList.SelectedItem as PurchaseDto)?.Id;
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
}
