using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Castar.Contents;

public partial class PurchasesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly ExpenseCategorizationService? categorization;
    private readonly PeriodClassification classification;
    private bool listeningForChanges;
    private readonly ObservableCollection<PurchaseRow> purchases = [];
    private ICollectionView? purchaseView;
    private string purchaseSearch = "";
    private bool uncategorizedOnly;
    private CancellationTokenSource? activeRequest;
    private CancellationTokenSource? comparisonRequest;
    private readonly PeriodComparison comparison = new(increaseIsFavorable: false);
    private CancellationTokenSource? detailsRequest;
    private CancellationTokenSource? avatarRequest;
    private CancellationTokenSource? purchaseAction;
    private (Guid Id, int Completed, int Total)? reclassificationProgress;
    private long connectionRevision;
    private bool hasLoaded;
    private bool refreshPending;
    private bool dataStale = true;
    private bool applyingPurchases;
    private PurchaseRow? detailsFor;
    private PurchaseRow? loadingDetailsFor;
    private Guid? notificationTarget;
    private SelectorRangeDates? periodSelector;
    internal SelectorRangeDates Dates => periodSelector
        ?? throw new InvalidOperationException("Период рабочего пространства ещё не подключён.");

    internal void BindPeriodSelector(SelectorRangeDates selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (ReferenceEquals(periodSelector, selector))
            return;
        if (periodSelector is not null)
            periodSelector.DateRangeChanged -= Dates_DateRangeChanged;
        periodSelector = selector;
        selector.DateRangeChanged += Dates_DateRangeChanged;
        dataStale = true;
    }

    public PurchasesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates,
        ExpenseCategorizationService? categorization = null)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        this.categorization = categorization;
        InitializeComponent();
        classification = new(ClassificationButton, server, settings, categorization, BudgetEvents.Purchase, "Покупки");
        DetailsPanel.DataContextChanged += (_, _) =>
        {
            UpdatePurchaseClassificationButton();
            UpdatePurchaseItemQueueStatus();
        };
        UpdatePurchaseClassificationButton();
        ExpenseComparison.DataContext = comparison;
        SelectionAmount.EnableSelectAll(PurchasesList);
        SelectionAmount.EnableSelectAll(ItemsList);
        purchaseView = CollectionViewSource.GetDefaultView(purchases);
        purchaseView.Filter = PurchaseMatches;
        PurchasesList.ItemsSource = purchaseView;
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
        if (!IsVisible || listeningForChanges || periodSelector is null)
            return;

        listeningForChanges = true;
        if (pendingPurchaseItems is null)
            ResumePurchaseItemQueue();
        if (dataStale)
            await RefreshAsync(showProgress: true);
        else
        {
            classification.Refresh(Dates.RangeDate.StartDate, Dates.RangeDate.EndDate, force: false);
            await LoadSelectionAsync();
        }
    }

    private void StopListening()
    {
        listeningForChanges = false;
        if (comparisonRequest is not null)
            dataStale = true;
        CancelRefresh();
        Progress.IsLoading = false;
        purchaseAction?.Cancel();
        CancelDetails();
    }

    private async void Dates_DateRangeChanged(object? sender, EventArgs e)
    {
        dataStale = true;
        if (!Dates.HasCompleteRange && !listeningForChanges)
            classification.Clear();
        if (!listeningForChanges)
            return;

        if (Dates.HasCompleteRange)
            ClearPurchases();
        await RefreshAsync();
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        classification.Clear();
        notificationTarget = null;
        connectionRevision++;
        purchaseAction?.Cancel();
        CancelRefresh();
        dataStale = true;
        ClearPurchases();
        ResumePurchaseItemQueue();
        if (listeningForChanges)
            await RefreshAsync();
    }

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientSettings.LocalAi))
            UpdatePurchaseClassificationButton();
        if (e.PropertyName is not (nameof(ClientSettings.DisplayCurrency) or nameof(ClientSettings.DisplayTimeZoneId)))
            return;

        if (e.PropertyName == nameof(ClientSettings.DisplayCurrency))
            RefreshRecurringPurchasesAfterServerChange();
        dataStale = true;
        hasLoaded = false;
        if (listeningForChanges)
            await RefreshAsync();
    }

    private void ClearPurchases()
    {
        avatarRequest?.Cancel();
        hasLoaded = false;
        ShowPurchases([], "");
        comparison.SetComparison(null, settings.DisplayCurrency);
        _ = LoadSelectionAsync();
        ShowStatus("");
        Progress.IsLoading = false;
    }

    private void CancelRefresh()
    {
        refreshPending = false;
        comparisonRequest?.Cancel();
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

    internal void ShowDay(DateOnly day)
    {
        SetUncategorizedFilter(false);
        notificationTarget = null;
        Dates.RangeDate = new RangeDate(day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue));
    }

    private async Task RefreshAsync(bool showProgress = false)
    {
        CancelRefresh();
        dataStale = true;
        if (!Dates.HasCompleteRange)
        {
            classification.Clear();
            ClearPurchases();
            return;
        }
        var range = Dates.RangeDate;
        var target = settings.DisplayCurrency;
        if (!hasLoaded)
            comparison.SetComparison(null, target);
        if (range.StartDate > range.EndDate)
        {
            ShowStatus("Дата начала позже даты окончания.");
            Progress.IsLoading = false;
            return;
        }
        classification.Refresh(range.StartDate, range.EndDate);

        using var request = new CancellationTokenSource();
        activeRequest = request;
        Progress.IsLoading = showProgress || !hasLoaded;
        try
        {
            var rows = await server.GetPurchaseSummariesAsync(range.StartDate, range.EndDate, request.Token,
                requireCategoryCoverage: uncategorizedOnly);
            if (request != activeRequest || refreshPending)
                return;

            rows = rows.OrderByDescending(purchase => purchase.AddedOn?.Date)
                .ThenByDescending(purchase => purchase.AddedOnUtc?.UtcDateTime ?? purchase.AddedOn)
                .ThenByDescending(purchase => purchase.Id).ToArray();
            var views = await ConvertPurchasesAsync(rows, target, request.Token);
            if (request != activeRequest || refreshPending)
                return;
            dataStale = false;
            ShowPurchases(views, target);
            _ = RefreshComparisonAsync(views, range, target);
            hasLoaded = true;
            ShowStatus("");
            _ = RefreshOwnerAvatarsAsync(rows);
            SelectNotificationTarget();
            notificationTarget = null;
            _ = LoadSelectionAsync();
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == activeRequest && IsAccessDenied(error))
                ShowAccessDenied(error);
            else if (request == activeRequest && !refreshPending && (showProgress || !hasLoaded))
                ShowStatus(CastarServerClient.DescribeError(error));
        }
        finally
        {
            if (request == activeRequest)
            {
                activeRequest = null;
                if (refreshPending)
                    _ = RefreshAsync(showProgress);
                else
                    Progress.IsLoading = false;
            }
        }
    }

    private async Task<PurchaseRow[]> ConvertPurchasesAsync(PurchaseDto[] rows, string target, CancellationToken token)
    {
        var dated = rows.SelectMany(row => NativeAmounts(row).Select(amount => amount.Currency)
            .Concat(row.Items.Select(item => item.Currency ?? row.Currency))
            .Select(currency => (currency, Date: OperationDate(row)))).Where(row => row.Date.HasValue);
        var rates = await exchangeRates.GetAsync(dated.Select(row => (row.currency, row.Date!.Value)), target, token);
        token.ThrowIfCancellationRequested();
        return rows.Select(row => new PurchaseRow(row, target, rates, null)).ToArray();
    }

    private async Task RefreshComparisonAsync(PurchaseRow[] rows, RangeDate range, string target)
    {
        comparisonRequest?.Cancel();
        using var request = new CancellationTokenSource();
        comparisonRequest = request;
        comparison.IsComparisonLoading = true;
        try
        {
            if (!HistoricalExchangeRates.IsCurrencyCode(target) || rows.Any(row => row.AmountWarning is not null))
            {
                comparison.SetComparison(null, target, "Для сравнения нужны валюта и курсы");
                return;
            }
            if (PeriodComparison.PreviousPeriod(range.StartDate, range.EndDate) is not { } previous)
            {
                comparison.SetComparison(null, target, "Нет предыдущего периода");
                return;
            }

            var summaries = await server.GetPurchaseSummariesAsync(previous.StartDate, previous.EndDate, request.Token);
            var previousRows = await ConvertPurchasesAsync(summaries, target, request.Token);
            if (request.IsCancellationRequested || comparisonRequest != request)
                return;

            if (previousRows.Any(row => row.AmountWarning is not null))
                comparison.SetComparison(null, target, "Для предыдущего периода нет валюты или курса");
            else
                comparison.SetComparison(rows.Sum(row => row.DisplayTotal) - previousRows.Sum(row => row.DisplayTotal), target);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error) || error is OverflowException)
        {
            if (comparisonRequest == request && !request.IsCancellationRequested)
                comparison.SetComparison(null, target, "Не удалось сравнить периоды");
        }
        finally
        {
            if (comparisonRequest == request)
            {
                comparisonRequest = null;
                comparison.IsComparisonLoading = false;
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
                ApplyAvatar(row, images);
            if (DetailsPanel.DataContext is PurchaseRow details)
                ApplyAvatar(details, images);
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

    private static void ApplyAvatar(PurchaseRow row, IReadOnlyDictionary<Guid, byte[]> images)
    {
        if (row.Source.Owner?.HasAvatar != true)
            row.SetOwnerAvatar(null);
        else if (images.TryGetValue(row.Source.UserId, out var image))
            row.SetOwnerAvatar(image);
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

    private static IReadOnlyList<PurchaseAmountDto> NativeAmounts(PurchaseDto purchase) =>
        purchase.AmountsByCurrency ?? [new(purchase.TotalSum, purchase.Currency)];

    private static decimal? RateFor(string? currency, DateOnly? date, string target,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        if (target.Length != 0 && target == currency)
            return 1m;
        return currency is not null && date is { } day && rates.TryGetValue((currency, day), out var rate) ? rate : null;
    }

    private static string[] FormatPurchaseTotals(IEnumerable<PurchaseRow> rows) =>
        rows.SelectMany(row => row.SelectionAmounts())
            .Where(row => row.Amount.HasValue && HistoricalExchangeRates.IsCurrencyCode(row.Currency))
            .GroupBy(row => row.Currency)
            .Select(group => HistoricalExchangeRates.Format(group.Sum(row => row.Amount!.Value), group.Key)).ToArray();

    private void ShowPurchases(PurchaseRow[] rows, string target)
    {
        var totals = FormatPurchaseTotals(rows);
        ExpenseNativeTotals.ItemsSource = totals.Length > 1 ? totals : null;
        var excludedCount = rows.Count(row => row.AmountWarning is not null);
        ExpenseTotal.Text = totals.Length == 1 ? totals[0] : excludedCount > 0 && totals.Length == 0 ? "Итог недоступен" : "";
        ExpenseTotalWarning.ToolTip = excludedCount > 0
            ? $"Покупок вне итога за период: {excludedCount} из {rows.Length}."
            : null;
        UpdateEmptyPurchases();
        if (purchases.Count == rows.Length && purchases.Zip(rows).All(pair => pair.First.EquivalentTo(pair.Second)))
            return;

        var selectedIds = PurchasesList.SelectedItems.OfType<PurchaseRow>().Select(row => row.Id).ToHashSet();
        var current = purchases.ToDictionary(row => row.Id);
        var receivedIds = rows.Select(row => row.Id).ToHashSet();
        applyingPurchases = true;
        try
        {
            for (var index = purchases.Count - 1; index >= 0; index--)
                if (!receivedIds.Contains(purchases[index].Id))
                    purchases.RemoveAt(index);

            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (!current.TryGetValue(row.Id, out var previous))
                {
                    purchases.Insert(index, row);
                    continue;
                }

                if (!ReferenceEquals(purchases[index], previous))
                    purchases.Move(purchases.IndexOf(previous), index);
                if (previous.EquivalentTo(row))
                    continue;
                if (previous.Source.UserId == row.Source.UserId && row.Source.Owner?.HasAvatar == true)
                    row.SetOwnerAvatar(previous.OwnerAvatar);
                purchases[index] = row;
            }

            if (selectedIds.Count > 0)
                foreach (var row in PurchasesList.Items.OfType<PurchaseRow>().Where(row => selectedIds.Contains(row.Id)))
                    if (!PurchasesList.SelectedItems.Contains(row))
                        PurchasesList.SelectedItems.Add(row);
        }
        finally
        {
            applyingPurchases = false;
            UpdatePurchaseDaySeparators();
            UpdatePurchaseSelection();
            UpdateEmptyPurchases();
        }
    }

    private void ShowStatus(string status)
    {
        StatusText.Text = status;
        StatusText.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyPurchases();
    }

    private static bool IsAccessDenied(Exception error) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    private void ShowAccessDenied(Exception error)
    {
        dataStale = true;
        CancelRefresh();
        ClearPurchases();
        ShowStatus(CastarServerClient.DescribeError(error));
    }

    private async void PurchasesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingPurchases || !ReferenceEquals(e.Source, sender))
            return;
        UpdatePurchaseSelection();
        await LoadSelectionAsync();
    }

    private void UpdatePurchaseSelection()
    {
        SelectionAmount.Show(SelectionTotal, SelectionTotalText, PurchasesList.SelectedItems.OfType<PurchaseRow>()
            .SelectMany(row => row.SelectionAmounts()));
        UpdatePurchaseItemQueueStatus();
    }

    private void ItemsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender))
            return;
        SelectionAmount.Show(GoodsSelectionTotal, GoodsSelectionTotalText, ItemsList.SelectedItems.OfType<ItemRow>()
            .Select(row => (row.SelectionTotal, row.SelectionCurrency)));
    }

    private void CancelDetails()
    {
        var previous = detailsRequest;
        detailsRequest = null;
        loadingDetailsFor = null;
        previous?.Cancel();
        if (DetailsPanel.Tag is DetailStatus { IsLoading: true })
            DetailsPanel.Tag = null;
    }

    private async Task LoadSelectionAsync()
    {
        UpdatePurchaseClassificationButton();
        var selected = PurchasesList.SelectedItem as PurchaseRow;
        if (selected is not null && (selected == detailsFor || selected == loadingDetailsFor))
            return;

        CancelDetails();
        DetailsPanel.Tag = null;
        if (selected is not { Source.ItemsDeferred: true })
        {
            detailsFor = selected;
            DetailsPanel.DataContext = selected;
            return;
        }

        if (DetailsPanel.DataContext is not PurchaseRow displayed || displayed.Id != selected.Id)
        {
            detailsFor = null;
            DetailsPanel.DataContext = selected;
        }
        if (!listeningForChanges)
            return;

        using var request = new CancellationTokenSource();
        detailsRequest = request;
        loadingDetailsFor = selected;
        if (detailsFor is null)
            DetailsPanel.Tag = new DetailStatus(true, "");
        try
        {
            var detail = await server.GetPurchaseDetailsAsync(selected.Id, request.Token);
            if (request == detailsRequest && !request.IsCancellationRequested)
            {
                var views = await ConvertPurchasesAsync([detail], selected.Target, request.Token);
                if (request != detailsRequest || request.IsCancellationRequested)
                    return;
                var row = views[0];
                row.SetOwnerAvatar(selected.OwnerAvatar);
                if (DetailsPanel.DataContext is not PurchaseRow rendered || !rendered.EquivalentTo(row))
                    DetailsPanel.DataContext = row;
                detailsFor = selected;
            }
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == detailsRequest && IsAccessDenied(error))
                ShowAccessDenied(error);
            else if (request == detailsRequest && !request.IsCancellationRequested && detailsFor is null)
                DetailsPanel.Tag = new DetailStatus(false, CastarServerClient.DescribeError(error));
        }
        finally
        {
            if (request == detailsRequest)
            {
                detailsRequest = null;
                loadingDetailsFor = null;
                if (DetailsPanel.Tag is DetailStatus { IsLoading: true })
                    DetailsPanel.Tag = null;
            }
        }
    }

    private sealed record DetailStatus(bool IsLoading, string Message);

    private PurchaseRow? ReclassifiablePurchase =>
        DetailsPanel.DataContext is PurchaseRow { CanDelete: true, Source.ItemsDeferred: false } row
        && PurchasesList.SelectedItem is PurchaseRow current && current.Id == row.Id
        && current.Source.Revision == row.Source.Revision && row.Source.Items.Count > 0
            ? row : null;

    private void UpdatePurchaseClassificationButton()
    {
        var modelConfigured = categorization is not null && !string.IsNullOrWhiteSpace(settings.LocalAi.Model);
        var eligible = ReclassifiablePurchase is not null;
        ReclassifyPurchaseButton.IsAvailable = modelConfigured && eligible && purchaseAction is null;
        if (reclassificationProgress is { } progress &&
            PurchasesList.SelectedItem is PurchaseRow selected && selected.Id == progress.Id)
        {
            ReclassifyPurchaseButton.IsBusy = true;
            ReclassifyPurchaseButton.ShowProgress(progress.Completed, progress.Total);
        }
        else
        {
            ReclassifyPurchaseButton.IsBusy = false;
            ReclassifyPurchaseButton.ProgressText = "";
        }
        ReclassifyPurchaseButton.ToolTip = !eligible ? null : modelConfigured
            ? "Повторить категоризацию покупки" : "Выберите модель в настройках автоматической категоризации.";
    }

    private async void ReclassifyPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (categorization is null || !ReclassifyPurchaseButton.IsAvailable || ReclassifiablePurchase is not { } row)
            return;

        await RunPurchaseActionAsync(async token =>
        {
            var total = row.Source.Items.Count;
            reclassificationProgress = (row.Id, 0, total);
            UpdatePurchaseClassificationButton();
            var report = new Progress<int>(completed =>
            {
                if (token.IsCancellationRequested || purchaseAction?.Token != token || reclassificationProgress is null)
                    return;
                reclassificationProgress = (row.Id, completed, total);
                UpdatePurchaseClassificationButton();
            });
            var applied = false;
            string? message = null;
            try
            {
                applied = await categorization.ReclassifyPurchaseAsync(row.Source, token, report);
            }
            catch (InvalidOperationException error) when (!token.IsCancellationRequested)
            {
                message = error.Message;
            }
            finally
            {
                reclassificationProgress = null;
                UpdatePurchaseClassificationButton();
            }
            if (!token.IsCancellationRequested && !applied)
                await ShowPurchaseActionMessageAsync(message ??
                    "Повторная категоризация недоступна. Проверьте версию Castar Server.");
        });
    }

    internal void RevealNotification(Guid id, DateTime? addedOn)
    {
        SetUncategorizedFilter(false);
        notificationTarget = id;
        if (!Dates.IncludeOperationDate(addedOn) && !SelectNotificationTarget())
            _ = RefreshAfterServerChangeAsync();
    }

    private bool SelectNotificationTarget()
    {
        if (notificationTarget is not { } id || purchases.FirstOrDefault(row => row.Id == id) is not { } selected)
            return false;
        notificationTarget = null;
        PurchaseSearchBox.Clear();
        PurchasesList.SelectedItem = selected;
        PurchasesList.ScrollIntoView(selected);
        return true;
    }

    private void Purchases_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void PurchaseActions_Click(object sender, RoutedEventArgs e)
    {
        if (purchaseAction is not null || sender is not Button { ContextMenu: { } menu, Tag: { } row } button ||
            row is not (PurchaseRow { CanDelete: true } or PurchaseRow { HasReceiptSource: true } or ItemRow))
            return;
        menu.DataContext = row;
        menu.Tag = new ActionMenuSnapshot(row, connectionRevision);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private T? GetMenuRow<T>(object sender) where T : class
    {
        if (sender is not MenuItem { Parent: ContextMenu { Tag: ActionMenuSnapshot snapshot } menu } ||
            snapshot.ConnectionRevision != connectionRevision)
            return null;
        menu.IsOpen = false;
        return snapshot.Row as T;
    }

    private sealed record ActionMenuSnapshot(object Row, long ConnectionRevision);

    private void ClearPurchaseSearch_Click(object sender, RoutedEventArgs e)
    {
        PurchaseSearchBox.Clear();
        PurchaseSearchBox.Focus();
    }

    private void PurchaseSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        purchaseSearch = PurchaseSearchBox.Text.Trim();
        if (PurchaseSearchPlaceholder is not null)
            PurchaseSearchPlaceholder.Visibility = purchaseSearch.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPurchaseFilter();
    }

    internal void ShowUncategorized()
    {
        PurchaseSearchBox.Clear();
        SetUncategorizedFilter(true);
    }

    private void ClearUncategorizedFilter_Click(object sender, RoutedEventArgs e) => SetUncategorizedFilter(false);

    private void SetUncategorizedFilter(bool enabled)
    {
        if (uncategorizedOnly == enabled)
            return;

        uncategorizedOnly = enabled;
        UncategorizedFilterButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshPurchaseFilter();
        if (enabled && (activeRequest is not null || purchases.Any(row => row.HasUncategorizedItems is null)))
        {
            dataStale = true;
            if (listeningForChanges)
                _ = RefreshAsync(showProgress: true);
        }
    }

    private void RefreshPurchaseFilter()
    {
        purchaseView?.Refresh();
        UpdatePurchaseDaySeparators();
        UpdateEmptyPurchases();
    }

    private void UpdatePurchaseDaySeparators()
    {
        if (purchaseView is null)
            return;

        var days = purchaseView.OfType<PurchaseRow>().GroupBy(row => row.PurchaseDay);
        foreach (var day in days)
        {
            if (day.Key is null)
                continue;
            var totals = FormatPurchaseTotals(day);
            var excluded = day.Count(row => row.AmountWarning is not null);
            var total = totals.Length > 0 ? string.Join(" · ", totals) : "Итог недоступен";
            var warning = excluded > 0 ? $"Покупок вне итога за день: {excluded} из {day.Count()}." : null;
            var first = true;
            foreach (var row in day)
            {
                row.SetPurchaseDayTotal(first ? total : null, first ? warning : null);
                first = false;
            }
        }
    }

    private bool PurchaseMatches(object item) => item is PurchaseRow row &&
        (!uncategorizedOnly || row.HasUncategorizedItems == true) &&
        (purchaseSearch.Length == 0 ||
            new[] { row.Name, row.OwnerName, row.RetailPlace, row.RetailPlaceAddress, row.Amount.Primary, row.Amount.Original,
                row.AddedOn?.ToString("dd.MM.yyyy HH:mm") }.Any(value =>
                value?.Contains(purchaseSearch, StringComparison.CurrentCultureIgnoreCase) == true));

    private void UpdateEmptyPurchases()
    {
        if (EmptyPurchases is null)
            return;
        EmptyPurchases.Text = purchaseSearch.Length != 0 ? "Ничего не найдено"
            : uncategorizedOnly ? "Нет покупок с товарами без категории" : "Нет покупок";
        EmptyPurchases.Visibility = hasLoaded && StatusText.Text.Length == 0 && purchaseView?.IsEmpty == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsActionElement(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase)
                return true;
        return false;
    }

    private async void PurchaseRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is DependencyObject origin && IsActionElement(origin) || purchaseAction is not null ||
            sender is not ListViewItem { DataContext: PurchaseRow { Source.Revision: { } revision } row })
            return;

        e.Handled = true;
        await EditPurchaseRowAsync(row);
    }

    private async void EditPurchaseMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is { } row)
            await EditPurchaseRowAsync(row, sender);
    }

    private Task EditPurchaseRowAsync(PurchaseRow row, object? source = null)
    {
        if (row.Source.Revision is not { } revision || purchaseAction is not null)
            return Task.CompletedTask;
        return RunPurchaseActionAsync(async token =>
        {
            PurchaseEditor? editor = null;
            await ButtonLoading.RunAsync(source, async () =>
            {
                var owners = row.Source.Owner is not null ? server.GetUsersAsync(token) : null;
                var users = owners is null ? null : await owners;
                token.ThrowIfCancellationRequested();
                editor = new PurchaseEditor(row.Source, owners: users);
            });
            if (editor is null || token.IsCancellationRequested)
                return;
            await ConfirmAndApplyAsync("Изменить покупку", editor, "Сохранить",
                request => server.EditPurchaseAsync(row.Id, revision, editor.Result!, request), token, editor.Validate, source: source,
                editorIcon: (Geometry)editor.FindResource("Lucide.pencil"));
        });
    }

    private async void PurchaseItemRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is DependencyObject origin && IsActionElement(origin) || purchaseAction is not null ||
            sender is not ListViewItem { DataContext: ItemRow row })
            return;

        e.Handled = true;
        await EditPurchaseItemRowAsync(row);
    }

    private async void EditPurchaseItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<ItemRow>(sender) is { } row)
            await EditPurchaseItemRowAsync(row, sender);
    }

    private Task EditPurchaseItemRowAsync(ItemRow row, object? source = null)
    {
        if (purchaseAction is not null)
            return Task.CompletedTask;
        if (row.Revision is not { } revision)
            return RunPurchaseActionAsync(_ => ShowPurchaseActionMessageAsync(
                "Сервер не передал версию покупки. Обновите список и откройте товар снова.", "Редактирование товара недоступно"));
        return ShowPurchaseItemEditorAsync(row.Source, row.Currency, row.SupportsCurrencyEdit, "Изменить товар", "Сохранить",
            null, source, (item, token) => server.EditPurchaseItemAsync(row.PurchaseId, row.Id, revision, item, token),
            amountsReadOnly: row.IsFiscalReceipt);
    }

    private Task ShowPurchaseItemEditorAsync(PurchaseItemDto item, string? currency, bool supportsCurrencyEdit, string title, string action,
        Func<PurchaseItemWriteDto, ValidatePopupDialog>? enqueue, object? source,
        Func<PurchaseItemWriteDto, CancellationToken, Task<bool>>? mutate = null, bool amountsReadOnly = false) =>
        RunPurchaseActionAsync(async token =>
        {
            IReadOnlyList<BudgetCategoryDto>? categories = null;
            ManualCurrencySettingsDto? currencies = null;
            string? currencyRestriction = null;
            await ButtonLoading.RunAsync(source, async () =>
            {
                var categoryRequest = server.GetOperationCategoriesAsync(BudgetEvents.Purchase, token);
                var settingsRequest = supportsCurrencyEdit && !amountsReadOnly ? server.GetServerSettingsAsync(token) : null;
                await Task.WhenAll(categoryRequest, settingsRequest ?? Task.CompletedTask);
                categories = await categoryRequest;
                if (amountsReadOnly)
                    return;
                if (settingsRequest is not null && await settingsRequest is { SupportsItemCurrencies: true } configuration)
                {
                    if (configuration.ManualCurrency is { Currencies: { } codes } policy && codes.Any(HistoricalExchangeRates.IsCurrencyCode))
                        currencies = policy;
                    else
                        currencyRestriction = "Список валют пуст. Проверьте разрешённые валюты в настройках сервера.";
                }
                else
                    currencyRestriction = "Выбор валюты товара недоступен на этой версии сервера. Обновите Castar Server.";
            });
            token.ThrowIfCancellationRequested();

            var editor = new PurchaseItemEditor(item, currency, usePurchaseFormStyle: true, categories: categories,
                allowedCurrencies: currencies?.Currencies, defaultCurrency: currencies?.DefaultCurrency,
                currencyReadOnly: currencies is null, preserveInheritedCurrency: item.Id != Guid.Empty,
                amountsReadOnly: amountsReadOnly, currencyRestriction: currencyRestriction);
            if (enqueue is not null)
            {
                var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
                {
                    options.Title = title;
                    options.EditorIcon = (Geometry)editor.FindResource("Lucide.package");
                    options.PreferredWidth = 480;
                    options.SuccessButton.SuccessConfirmButtonText = action;
                    options.AllowMissClick = false;
                    return options;
                }, () =>
                {
                    var validation = editor.Validate();
                    if (!validation.Result)
                        return validation;
                    return token.IsCancellationRequested
                        ? new(false, "Соединение изменилось. Откройте покупку заново.")
                        : enqueue(editor.Result!);
                });
                if (accepted == true)
                    _ = ProcessPurchaseItemQueueAsync();
                return;
            }
            var saved = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Title = title;
                options.EditorIcon = (Geometry)editor.FindResource("Lucide.package");
                options.PreferredWidth = 487;
                options.SuccessButton.SuccessConfirmButtonText = action;
                options.AllowMissClick = false;
                options.ConfirmAsync = async () =>
                {
                    try
                    {
                        return await mutate!(editor.Result!, token) ? new(true, null) : new(false, "Товар больше недоступен.");
                    }
                    catch (Exception error) when (CastarServerClient.IsConnectionError(error))
                    {
                        return new(false, CastarServerClient.DescribeError(error));
                    }
                };
                return options;
            }, editor.Validate);
            if (saved == true)
                await RefreshAfterPurchaseMutationAsync(token);
        });

    private async void DeletePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is not { Source.Revision: { } revision } row)
            return;

        await ConfirmMutationAsync("Удалить покупку", $"«{row.Name}» и все её товары будут удалены.", "Удалить",
            token => server.DeletePurchaseAsync(row.Id, revision, token), foreground: Brushes.OrangeRed, source: sender);
    }

    private async void DeletePurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<ItemRow>(sender) is not { CanDelete: true, Revision: { } revision } row)
            return;

        var message = $"Товар «{row.Name}» будет удалён.";
        await ConfirmMutationAsync("Удалить товар", message, "Удалить",
            token => server.DeletePurchaseItemAsync(row.PurchaseId, row.Id, revision, token), foreground: Brushes.OrangeRed, source: sender);
    }

    private Task ConfirmMutationAsync(string title, object content, string action, Func<CancellationToken, Task<bool>> mutate,
        Func<ValidatePopupDialog>? validate = null, Brush? foreground = null, object? source = null, Geometry? editorIcon = null) =>
        RunPurchaseActionAsync(token => ConfirmAndApplyAsync(title, content, action, mutate, token, validate, foreground, source, editorIcon));

    private async Task ConfirmAndApplyAsync(string title, object content, string action,
        Func<CancellationToken, Task<bool>> mutate, CancellationToken token,
        Func<ValidatePopupDialog>? validate = null, Brush? foreground = null, object? source = null,
        Geometry? editorIcon = null)
    {
        var accepted = await PopupDialog.Show(content, PopupDialog.TypeDialogEnum.Confirm, options =>
        {
            options.Title = title;
            options.EditorIcon = editorIcon;
            options.SuccessButton.SuccessConfirmButtonText = action;
            if (foreground is not null)
                options.SuccessButton.SuccessConfirmButtonForeground = foreground;
            options.AllowMissClick = false;
            return options;
        }, validate);
        if (accepted != true || token.IsCancellationRequested)
            return;

        bool? updated = null;
        await ButtonLoading.RunAsync(source, async () =>
        {
            updated = await mutate(token);
            await RefreshAfterPurchaseMutationAsync(token);
        });
        if (updated == false && !token.IsCancellationRequested)
            await ShowPurchaseActionMessageAsync("Покупка или позиция уже удалена.");
    }

    private static Task<bool?> ShowPurchaseActionMessageAsync(string message, string title = "Расходы") =>
        PopupDialog.Show(message, PopupDialog.TypeDialogEnum.Default, options =>
        {
            options.Title = title;
            return options;
        });

    private async void ShowReceiptSource_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuRow<PurchaseRow>(sender) is not { HasReceiptSource: true } row)
            return;

        await RunPurchaseActionAsync(async token =>
        {
            object? content = null;
            await ButtonLoading.RunAsync(sender, async () =>
            {
                var source = await server.GetPurchaseReceiptSourceAsync(row.Id, token);
                token.ThrowIfCancellationRequested();
                content = source?.Content is { Length: > 0 } raw
                    ? new ReceiptSourcePanel(raw, source.IsLegacy)
                    : "Исходные данные для этой покупки недоступны.";
            });
            if (content is null || token.IsCancellationRequested)
                return;
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
        UpdatePurchaseClassificationButton();
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
            UpdatePurchaseClassificationButton();
        }
    }

    private async Task RefreshAfterPurchaseMutationAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested || !listeningForChanges)
            return;
        await RefreshAsync();
    }

    private sealed class PurchaseRow : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<PurchaseAmountDto> nativeAmounts;

        public PurchaseRow(PurchaseDto source, string target,
            IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates, byte[]? ownerAvatar)
        {
            Source = source;
            Target = target;
            OwnerAvatar = ownerAvatar;
            nativeAmounts = NativeAmounts(source);
            var date = OperationDate(source);
            var amounts = nativeAmounts.Select(amount =>
                (amount.Amount, amount.Currency, Rate: RateFor(amount.Currency, date, target, rates))).ToArray();
            foreach (var amount in amounts)
            {
                AmountWarning = GetAmountWarning(amount.Currency, date, target, amount.Rate);
                if (AmountWarning is not null)
                    break;
            }

            if (amounts.Length == 1)
            {
                var amount = amounts[0];
                DisplayTotal = ConvertAmount(amount.Amount, amount.Currency, target, amount.Rate);
                Amount = FormatAmount(amount.Amount, amount.Currency, target, amount.Rate);
            }
            else
            {
                var original = HistoricalExchangeRates.FormatTotals(nativeAmounts.Select(amount => (amount.Amount, amount.Currency)));
                if (target.Length == 0 || AmountWarning is not null)
                    Amount = new(original, null);
                else
                {
                    DisplayTotal = amounts.Sum(amount => ConvertAmount(amount.Amount, amount.Currency, target, amount.Rate));
                    Amount = new(HistoricalExchangeRates.Format(DisplayTotal, target), original);
                }
            }

            Items = source.Items.Select(item =>
            {
                var currency = item.Currency ?? source.Currency;
                var rate = RateFor(currency, date, target, rates);
                return new ItemRow(source.Id, item, ItemCurrencyFallback, source.Revision, source.SupportsItemCurrencyEdit,
                    IsFiscalReceipt,
                    FormatAmount(item.Price, currency, target, rate), FormatAmount(item.Sum, currency, target, rate),
                    GetAmountWarning(currency, date, target, rate) is null ? ConvertAmount(item.Sum, currency, target, rate) : null,
                    target.Length == 0 ? currency : target);
            }).ToArray();
            UncategorizedItemCount = source.Items.Count(item => item.BudgetCategoryId is null);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public PurchaseDto Source { get; }
        public string Target { get; }
        public Guid Id => Source.Id;
        public string Name => Source.Name;
        public bool IsEdited => Source.IsEdited;
        public string? OwnerName => Source.Owner?.Name;
        public byte[]? OwnerAvatar { get; private set; }
        public bool CanDelete => Source.Revision.HasValue;
        public bool CanAddItems => CanDelete && !IsFiscalReceipt;
        public string? AddItemRestriction => IsFiscalReceipt ? "Состав покупки загружен из фискального чека. Добавлять товары в него нельзя."
            : CanDelete ? null : "Сервер не передал версию покупки. Обновите список перед добавлением товара.";
        private bool IsFiscalReceipt => Source.IsFiscalReceipt || Source.ReceiptDate.HasValue;
        public bool HasReceiptSource => Source.HasReceiptSource;
        public string? ItemCurrencyFallback => Source.Currency ?? (nativeAmounts.Count == 1 ? nativeAmounts[0].Currency : null);
        public DateTime? AddedOn => Source.AddedOn;
        public DateOnly? PurchaseDay => AddedOn is { } time ? DateOnly.FromDateTime(time) : null;
        public string? PurchaseDayLabel => PurchaseDay?.Day.ToString();
        public bool StartsPurchaseDay => DayTotal is not null;
        public string? DayTotal { get; private set; }
        public string? DayTotalWarning { get; private set; }
        public string? RetailPlace => string.IsNullOrWhiteSpace(Source.RetailPlace) ? null : Source.RetailPlace;
        public string? RetailPlaceAddress => string.IsNullOrWhiteSpace(Source.RetailPlaceAddress) ? null : Source.RetailPlaceAddress;
        public decimal DisplayTotal { get; }
        public MoneyAmountDisplay Amount { get; }
        public string? AmountWarning { get; }
        public int UncategorizedItemCount { get; }
        public bool? HasUncategorizedItems => Source.HasUncategorizedItems ??
            (Source.ItemsDeferred ? null : UncategorizedItemCount > 0);

        public ItemRow[] Items { get; }

        public IEnumerable<(decimal? Amount, string? Currency)> SelectionAmounts() => Target.Length == 0
            ? nativeAmounts.Select(amount => ((decimal?)amount.Amount, amount.Currency))
            : [(AmountWarning is null ? DisplayTotal : null, Target)];

        public bool EquivalentTo(PurchaseRow other) => Target == other.Target && Amount == other.Amount
            && AmountWarning == other.AmountWarning
            && (Source with { Items = other.Source.Items, AmountsByCurrency = other.Source.AmountsByCurrency }) == other.Source
            && nativeAmounts.SequenceEqual(other.nativeAmounts) && Items.SequenceEqual(other.Items);

        public void SetOwnerAvatar(byte[]? image)
        {
            if (OwnerAvatar.AsSpan().SequenceEqual(image))
                return;
            OwnerAvatar = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OwnerAvatar)));
        }

        public void SetPurchaseDayTotal(string? total, string? warning)
        {
            if (DayTotal == total && DayTotalWarning == warning)
                return;
            var startsDayChanged = StartsPurchaseDay != (total is not null);
            DayTotal = total;
            DayTotalWarning = warning;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DayTotal)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DayTotalWarning)));
            if (startsDayChanged)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartsPurchaseDay)));
        }

        private static string? GetAmountWarning(string? currency, DateOnly? operationDate, string target, decimal? rate)
        {
            if (!HistoricalExchangeRates.IsCurrencyCode(currency))
                return "Валюта отсутствует или недействительна.";
            if (target.Length == 0 || rate.HasValue)
                return null;
            return operationDate is { } date
                ? $"Нет курса {currency} → {target} на {date:dd.MM.yyyy}."
                : $"Нет даты покупки для курса {currency} → {target}.";
        }

        private static decimal ConvertAmount(decimal amount, string? source, string target, decimal? rate) =>
            rate is { } value && source != target ? HistoricalExchangeRates.ConvertAmount(amount, value) : amount;

        private static MoneyAmountDisplay FormatAmount(decimal amount, string? source, string target, decimal? rate) =>
            MoneyAmountDisplay.Create(amount, source, rate.HasValue ? ConvertAmount(amount, source, target, rate) : null, target);
    }

    private sealed record ItemRow(Guid PurchaseId, PurchaseItemDto Source, string? Currency, long? Revision,
        bool SupportsCurrencyEdit, bool IsFiscalReceipt, MoneyAmountDisplay Price, MoneyAmountDisplay Sum,
        decimal? SelectionTotal, string? SelectionCurrency)
    {
        public Guid Id => Source.Id;
        public bool CanDelete => Revision.HasValue && !IsFiscalReceipt;
        public string? DeleteItemRestriction => IsFiscalReceipt ? "Состав фискального чека фиксирован. Удалять товары из него нельзя." : null;
        public string Name => Source.Name;
        public double Quantity => Source.Quantity;
        public bool IsEdited => Source.IsEdited;
        public bool HasCategory => Source.BudgetCategoryId.HasValue;
        public string CategoryLabel => HasCategory ? Source.BudgetCategoryName ?? "Категория" : "Без категории";
        public string? CategoryIconKey => Source.BudgetCategoryIconKey;
    }
}
