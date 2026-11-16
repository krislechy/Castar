using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Concurrent;
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

namespace Castar.Contents;

public partial class PurchasesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly HistoricalExchangeRates exchangeRates;
    private bool listeningForChanges;
    private PurchaseRow[] purchases = [];
    private CancellationTokenSource? activeRequest;
    private CancellationTokenSource? detailsRequest;
    private CancellationTokenSource? avatarRequest;
    private CancellationTokenSource? purchaseAction;
    private long connectionRevision;
    private bool hasLoaded;
    private bool refreshPending;
    private bool dataStale = true;
    private Guid? notificationTarget;

    public PurchasesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        InitializeComponent();
        Dates.DisplayTimeZone = () => settings.DisplayTimeZone;
        Dates.SetCurrentMonth();
        server.ConnectionChanged += Server_ConnectionChanged;
        settings.PropertyChanged += Settings_PropertyChanged;
    }

    private async void Purchases_Loaded(object sender, RoutedEventArgs e) => await StartListeningAsync();

    private void Purchases_Unloaded(object sender, RoutedEventArgs e) => StopListening();

    private async void Purchases_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
            await StartListeningAsync();
        else
            StopListening();
    }

    private async Task StartListeningAsync()
    {
        if (!IsVisible || listeningForChanges)
            return;

        listeningForChanges = true;
        if (DetailsPanel.DataContext is PurchaseRow { Source.ItemsDeferred: true } && !dataStale)
            await LoadSelectionAsync();
        if (dataStale)
            await RefreshAsync();
    }

    private void StopListening()
    {
        listeningForChanges = false;
        CancelRefresh();
        Progress.IsLoading = false;
        purchaseAction?.Cancel();
        detailsRequest?.Cancel();
    }

    private async void Dates_DateRangeChanged(object sender, EventArgs e)
    {
        dataStale = true;
        if (!listeningForChanges)
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
        dataStale = true;
        ClearPurchases();
        if (listeningForChanges)
            await RefreshAsync();
    }

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ClientSettings.DisplayCurrency) or nameof(ClientSettings.DisplayTimeZoneId)))
            return;

        dataStale = true;
        if (listeningForChanges)
            await RefreshAsync();
    }

    private void ClearPurchases()
    {
        avatarRequest?.Cancel();
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
        dataStale = true;
        if (!listeningForChanges)
            return Task.CompletedTask;
        if (activeRequest is null)
            return RefreshAsync();

        refreshPending = true;
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        CancelRefresh();
        dataStale = true;
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
            var rows = await server.GetPurchaseSummariesAsync(range.StartDate, range.EndDate, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            rows = rows.OrderBy(purchase => purchase.AddedOnUtc?.UtcDateTime ?? purchase.AddedOn)
                .ThenBy(purchase => purchase.Id).ToArray();
            var target = settings.DisplayCurrency;
            var dated = rows.Select(row => (row.Currency, Date: OperationDate(row))).Where(row => row.Date.HasValue);
            var rates = await exchangeRates.GetAsync(dated.Select(row => (row.Currency, row.Date!.Value)), target, request.Token);
            if (request != activeRequest || refreshPending)
                return;
            var views = rows.Select(row => new PurchaseRow(row, target, RateFor(row, target, rates), null)).ToArray();
            dataStale = false;
            ShowPurchases(views, target);
            SelectNotificationTarget();
            notificationTarget = null;
            hasLoaded = true;
            ShowStatus("");
            _ = RefreshOwnerAvatarsAsync(rows);
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

    private async Task RefreshOwnerAvatarsAsync(PurchaseDto[] rows)
    {
        var previous = avatarRequest;
        using var request = new CancellationTokenSource();
        avatarRequest = request;
        previous?.Cancel();
        try
        {
            var images = await LoadOwnerAvatarsAsync(rows, request.Token);
            if (request != avatarRequest || request.IsCancellationRequested)
                return;
            foreach (var row in purchases)
                row.SetOwnerAvatar(images.GetValueOrDefault(row.Source.UserId));
            if (DetailsPanel.DataContext is PurchaseRow details)
                details.SetOwnerAvatar(images.GetValueOrDefault(details.Source.UserId));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        finally
        {
            if (request == avatarRequest)
                avatarRequest = null;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, byte[]>> LoadOwnerAvatarsAsync(PurchaseDto[] rows, CancellationToken token)
    {
        var owners = rows.Where(row => row.Owner?.HasAvatar == true).Select(row => row.UserId).Distinct();
        var avatars = new ConcurrentDictionary<Guid, byte[]>();
        await Parallel.ForEachAsync(owners, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 },
            async (owner, cancellation) =>
            {
                try
                {
                    if (await server.GetUserAvatarAsync(owner, cancellation) is { Length: > 0 } image)
                        avatars[owner] = image;
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested && CastarServerClient.IsConnectionError(error))
                {
                    // The owner still has initials if the optional image is unavailable.
                }
            });
        return avatars;
    }

    private static DateOnly? OperationDate(PurchaseDto purchase) => purchase.RateDate ?? purchase.ReceiptDate
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

    private async void PurchasesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadSelectionAsync();

    private async Task LoadSelectionAsync()
    {
        var previous = detailsRequest;
        detailsRequest = null;
        previous?.Cancel();
        var selected = PurchasesList.SelectedItem as PurchaseRow;
        DetailsPanel.DataContext = selected;
        DetailsPanel.Tag = null;
        if (!listeningForChanges || selected is not { Source.ItemsDeferred: true })
            return;

        using var request = new CancellationTokenSource();
        detailsRequest = request;
        DetailsPanel.Tag = new DetailStatus(true, "");
        try
        {
            var detail = await server.GetPurchaseDetailsAsync(selected.Id, request.Token);
            if (request == detailsRequest && !request.IsCancellationRequested)
                DetailsPanel.DataContext = selected.WithDetails(detail);
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == detailsRequest && !request.IsCancellationRequested)
                DetailsPanel.Tag = new DetailStatus(false, CastarServerClient.DescribeError(error));
        }
        finally
        {
            if (request == detailsRequest)
            {
                detailsRequest = null;
                if (DetailsPanel.Tag is DetailStatus { IsLoading: true })
                    DetailsPanel.Tag = null;
            }
        }
    }

    private sealed record DetailStatus(bool IsLoading, string Message);

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
        await RunPurchaseActionAsync(async token =>
        {
            var currencies = row.Source.SupportsCurrencyEdit
                ? (await server.GetServerSettingsAsync(token)).ManualCurrency?.Currencies
                : null;
            var owners = row.Source.Owner is not null ? await server.GetUsersAsync(token) : null;
            token.ThrowIfCancellationRequested();
            var editor = new PurchaseEditor(row.Source, currencies, owners);
            await ConfirmAndApplyAsync("Изменить покупку", editor, "Сохранить",
                request => server.EditPurchaseAsync(row.Id, revision, editor.Result!, request), token, editor.Validate);
        });
    }

    private async void PurchaseItemRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || purchaseAction is not null ||
            sender is not ListViewItem { DataContext: ItemRow { Revision: { } revision } row })
            return;

        e.Handled = true;
        var editor = new PurchaseItemEditor(row.Source, row.Currency);
        await ConfirmMutationAsync("Изменить товар", editor, "Сохранить",
            token => server.EditPurchaseItemAsync(row.PurchaseId, row.Id, revision, editor.Result!, token), validate: editor.Validate);
    }

    private async void DeletePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is not { Source.Revision: { } revision } row)
            return;

        await ConfirmMutationAsync("Удалить покупку", $"«{row.Name}» и все её товары будут удалены.", "Удалить",
            token => server.DeletePurchaseAsync(row.Id, revision, token), foreground: Brushes.OrangeRed);
    }

    private async void DeletePurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<ItemRow>(sender) is not { Revision: { } revision } row)
            return;

        var message = $"Товар «{row.Name}» будет удалён.";
        if (row.IsLast)
            message += " Итог покупки станет нулевым.";
        await ConfirmMutationAsync("Удалить товар", message, "Удалить",
            token => server.DeletePurchaseItemAsync(row.PurchaseId, row.Id, revision, token), foreground: Brushes.OrangeRed);
    }

    private Task ConfirmMutationAsync(string title, object content, string action, Func<CancellationToken, Task<bool>> mutate,
        Func<ValidatePopupDialog>? validate = null, Brush? foreground = null) =>
        RunPurchaseActionAsync(token => ConfirmAndApplyAsync(title, content, action, mutate, token, validate, foreground));

    private async Task ConfirmAndApplyAsync(string title, object content, string action,
        Func<CancellationToken, Task<bool>> mutate, CancellationToken token,
        Func<ValidatePopupDialog>? validate = null, Brush? foreground = null)
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
    }

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
                : "Исходные данные для этой покупки недоступны.";
            await PopupDialog.Show(content, PopupDialog.TypeDialogEnum.Default, options =>
            {
                options.Title = "Исходные данные";
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
        if (token.IsCancellationRequested || !listeningForChanges)
            return;
        await RefreshAsync();
    }

    private sealed class PurchaseRow(PurchaseDto source, string target, decimal? rate, byte[]? ownerAvatar) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public PurchaseDto Source { get; } = source;
        public decimal? Rate { get; } = rate;
        private string Target { get; } = target;
        public Guid Id => Source.Id;
        public string Name => Source.Name;
        public bool IsEdited => Source.IsEdited;
        public string? OwnerName => Source.Owner?.Name;
        public byte[]? OwnerAvatar { get; private set; } = ownerAvatar;
        public bool CanDelete => Source.Revision.HasValue;
        public bool HasReceiptSource => Source.HasReceiptSource;
        public DateTime? AddedOn => Source.AddedOn;
        public string? RetailPlace => string.IsNullOrWhiteSpace(Source.RetailPlace) ? null : Source.RetailPlace;
        public string? RetailPlaceAddress => string.IsNullOrWhiteSpace(Source.RetailPlaceAddress) ? null : Source.RetailPlaceAddress;
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

        public void SetOwnerAvatar(byte[]? image)
        {
            if (OwnerAvatar.AsSpan().SequenceEqual(image))
                return;
            OwnerAvatar = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OwnerAvatar)));
        }

        public PurchaseRow WithDetails(PurchaseDto detail) => new(detail, Target,
            detail.Currency == Source.Currency && OperationDate(detail) == OperationDate(Source) ? Rate : null, OwnerAvatar);

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
        public bool IsEdited => Source.IsEdited;
    }
}
