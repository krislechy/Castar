using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private CancellationTokenSource? purchaseAction;
    private long connectionRevision;
    private bool hasLoaded;
    private bool refreshPending;
    private Guid? notificationTarget;

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
        purchaseAction?.Cancel();
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
        notificationTarget = null;
        connectionRevision++;
        purchaseAction?.Cancel();
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
            SelectNotificationTarget();
            notificationTarget = null;
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
        var total = HistoricalExchangeRates.FormatTotals(amounts);
        var excludedCount = rows.Count(row => row.AmountWarning is not null);
        ExpenseTotal.Text = excludedCount > 0 && total.Length == 0 ? "Итог недоступен" : total;
        ExpenseTotalWarning.ToolTip = excludedCount > 0
            ? $"Покупок вне итога за период: {excludedCount} из {rows.Length}."
            : null;
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

    internal void RevealNotification(Guid id, DateTime? addedOn)
    {
        notificationTarget = id;
        if (!Dates.IncludeOperationDate(addedOn) && !SelectNotificationTarget())
            _ = RefreshAfterServerChangeAsync();
    }

    private bool SelectNotificationTarget()
    {
        if (notificationTarget is not { } id || purchases.FirstOrDefault(row => row.Id == id) is not { } selected)
            return false;
        notificationTarget = null;
        PurchasesList.SelectedItem = selected;
        PurchasesList.ScrollIntoView(selected);
        return true;
    }

    private void PurchaseActionMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (purchaseAction is not null || sender is ListViewItem item && item.DataContext is not
            (PurchaseRow { CanDelete: true } or PurchaseRow { HasReceiptSource: true } or ItemRow { Revision: not null }))
            e.Handled = true;
    }

    private void PurchaseActionMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.DataContext = (menu.PlacementTarget as ListViewItem)?.DataContext;
        menu.Tag = connectionRevision;
    }

    private T? GetMenuRow<T>(object sender) where T : class =>
        sender is MenuItem { Parent: ContextMenu menu } && Equals(menu.Tag, connectionRevision)
            ? menu.DataContext as T : null;

    private async void PurchaseRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || purchaseAction is not null ||
            sender is not ListViewItem { DataContext: PurchaseRow { Source.Revision: { } revision } row })
            return;

        e.Handled = true;
        var editor = new PurchaseEditor(row.Source);
        await ConfirmMutationAsync("Изменить покупку", editor, "Сохранить",
            token => server.EditPurchaseAsync(row.Id, revision, editor.Result!, token), validate: editor.Validate);
    }

    private async void PurchaseItemRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || purchaseAction is not null ||
            sender is not ListViewItem { DataContext: ItemRow { Revision: { } revision } row })
            return;

        e.Handled = true;
        var editor = new PurchaseItemEditor(row.Source, row.Currency);
        await ConfirmMutationAsync("Изменить позицию", editor, "Сохранить",
            token => server.EditPurchaseItemAsync(row.PurchaseId, row.Id, revision, editor.Result!, token), validate: editor.Validate);
    }

    private async void DeletePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is not { Source.Revision: { } revision } row)
            return;

        await ConfirmMutationAsync("Удалить покупку", $"«{row.Name}» и все её позиции будут удалены.", "Удалить",
            token => server.DeletePurchaseAsync(row.Id, revision, token), foreground: Brushes.OrangeRed);
    }

    private async void DeletePurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<ItemRow>(sender) is not { Revision: { } revision } row)
            return;

        var message = $"Позиция «{row.Name}» будет удалена.";
        if (row.IsLast)
            message += " Итог покупки станет нулевым.";
        await ConfirmMutationAsync("Удалить позицию", message, "Удалить",
            token => server.DeletePurchaseItemAsync(row.PurchaseId, row.Id, revision, token), foreground: Brushes.OrangeRed);
    }

    private Task ConfirmMutationAsync(string title, object content, string action, Func<CancellationToken, Task<bool>> mutate,
        Func<ValidatePopupDialog>? validate = null, Brush? foreground = null) =>
        RunPurchaseActionAsync(async token =>
        {
            var accepted = await PopupDialog.Show(content, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Title = title;
                options.SuccessButton.SuccessConfirmButtonText = action;
                if (foreground is not null)
                    options.SuccessButton.SuccessConfirmButtonForeground = foreground;
                options.AllowMissClick = false;
                return options;
            }, validate);
            if (accepted != true || token.IsCancellationRequested)
                return;

            var updated = await mutate(token);
            await RefreshAfterPurchaseMutationAsync(token);
            if (!updated && !token.IsCancellationRequested)
                await ShowPurchaseActionMessageAsync("Покупка или позиция уже удалена.");
        });

    private static Task<bool?> ShowPurchaseActionMessageAsync(string message) =>
        PopupDialog.Show(message, PopupDialog.TypeDialogEnum.Default, options =>
        {
            options.Title = "Расходы";
            return options;
        });

    private async void ShowReceiptSource_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is not { HasReceiptSource: true } row)
            return;

        await RunPurchaseActionAsync(async token =>
        {
            var source = await server.GetPurchaseReceiptSourceAsync(row.Id, token);
            token.ThrowIfCancellationRequested();
            object content = source?.Content is { Length: > 0 } raw
                ? new ReceiptSourcePanel(raw, source.IsLegacy)
                : "Исходный запрос для этой покупки недоступен.";
            await PopupDialog.Show(content, PopupDialog.TypeDialogEnum.Default, options =>
            {
                options.Title = "Исходный запрос";
                options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                options.DefaultButton.DefaultButtonText = "Закрыть";
                return options;
            });
        });
    }

    private async Task RunPurchaseActionAsync(Func<CancellationToken, Task> action)
    {
        if (purchaseAction is not null)
            return;

        using var request = new CancellationTokenSource();
        purchaseAction = request;
        try
        {
            await action(request.Token);
        }
        catch (HttpRequestException error) when (!request.IsCancellationRequested &&
            error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            await RefreshAfterPurchaseMutationAsync(request.Token);
            if (!request.IsCancellationRequested)
                await ShowPurchaseActionMessageAsync("Покупка изменилась. Проверьте обновлённый список.");
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (!request.IsCancellationRequested)
                await ShowPurchaseActionMessageAsync(CastarServerClient.DescribeError(error));
        }
        finally
        {
            purchaseAction = null;
        }
    }

    private async Task RefreshAfterPurchaseMutationAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested || !polling.IsEnabled)
            return;
        await RefreshAsync();
    }

    private sealed class PurchaseRow(PurchaseDto source, string target, decimal? rate)
    {
        public PurchaseDto Source { get; } = source;
        public decimal? Rate { get; } = rate;
        private string Target { get; } = target;
        public Guid Id => Source.Id;
        public string Name => Source.Name;
        public bool CanDelete => Source.Revision.HasValue;
        public bool HasReceiptSource => Source.HasReceiptSource;
        public DateTime? AddedOn => Source.AddedOn;
        public string? RetailPlace => Source.RetailPlace;
        public string? RetailPlaceAddress => Source.RetailPlaceAddress;
        public decimal DisplayTotal => Rate is { } value && Source.Currency != Target
            ? HistoricalExchangeRates.ConvertAmount(Source.TotalSum, value) : Source.TotalSum;
        public MoneyAmountDisplay Amount { get; } = FormatAmount(source.TotalSum, source.Currency, target, rate);
        public string? AmountWarning { get; } = GetAmountWarning(source, target, rate);

        public ItemRow[] Items { get; } = source.Items.Select(item => new ItemRow(source.Id, item, source.Currency, source.Revision,
            source.Items.Count == 1,
            FormatAmount(item.Price, source.Currency, target, rate),
            FormatAmount(item.Sum, source.Currency, target, rate))).ToArray();

        public bool EquivalentTo(PurchaseRow other) => Target == other.Target && Rate == other.Rate
            && (Source with { Items = other.Source.Items }) == other.Source
            && Source.Items.SequenceEqual(other.Source.Items);

        private static string? GetAmountWarning(PurchaseDto purchase, string target, decimal? rate)
        {
            if (!HistoricalExchangeRates.IsCurrencyCode(purchase.Currency))
                return "Валюта покупки отсутствует или недействительна.";
            if (target.Length == 0 || rate.HasValue)
                return null;
            return OperationDate(purchase) is { } date
                ? $"Нет курса {purchase.Currency} → {target} на {date:dd.MM.yyyy}."
                : $"Нет даты покупки для курса {purchase.Currency} → {target}.";
        }

        private static MoneyAmountDisplay FormatAmount(decimal amount, string? source, string target, decimal? rate) =>
            MoneyAmountDisplay.Create(amount, source,
                rate is { } value && source != target ? HistoricalExchangeRates.ConvertAmount(amount, value) : null, target);
    }

    private sealed record ItemRow(Guid PurchaseId, PurchaseItemDto Source, string? Currency, long? Revision,
        bool IsLast, MoneyAmountDisplay Price, MoneyAmountDisplay Sum)
    {
        public Guid Id => Source.Id;
        public string Name => Source.Name;
        public double Quantity => Source.Quantity;
    }
}
