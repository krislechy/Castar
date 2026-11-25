using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.Collections;
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
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Castar.Contents;

public partial class IncomesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly PeriodClassification classification;
    private readonly IncomesContext context = new();
    private bool listeningForChanges;
    private CancellationTokenSource? activeRequest;
    private CancellationTokenSource? comparisonRequest;
    private bool refreshPending;
    private bool dataStale = true;
    private (DateTime Start, DateTime Finish, string Currency, string TimeZone)? renderedQuery;
    private Guid? notificationTarget;
    private readonly HashSet<Guid> expandedGroups = [];
    private IReadOnlyDictionary<Guid, BudgetCategoryDto>? categoryNames;
    private string incomeSearch = "";
    private bool uncategorizedOnly;
    private bool applyingIncomes;
    private bool editingIncome;
    private long revealGeneration;
    private long incomeConnectionRevision;
    private SelectorRangeDates? periodSelector;
    internal SelectorRangeDates SelectorRangeDatesElement => periodSelector
        ?? throw new InvalidOperationException("Период рабочего пространства ещё не подключён.");
    private RangeDate Range => SelectorRangeDatesElement.RangeDate;

    internal void BindPeriodSelector(SelectorRangeDates selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (ReferenceEquals(periodSelector, selector))
            return;
        if (periodSelector is not null)
            periodSelector.DateRangeChanged -= SelectorRangeDates_DateRangeChanged;
        periodSelector = selector;
        selector.DateRangeChanged += SelectorRangeDates_DateRangeChanged;
        dataStale = true;
    }

    public IncomesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates,
        ExpenseCategorizationService? categorization = null)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        context.SummaryCurrency = settings.DisplayCurrency;
        DataContext = context;
        InitializeComponent();
        classification = new(ClassificationButton, server, settings, categorization, BudgetEvents.Income, "Доходы");
        SelectionAmount.EnableSelectAll(IncomesListView);
        server.ConnectionChanged += Server_ConnectionChanged;
        settings.PropertyChanged += Settings_PropertyChanged;
        IsVisibleChanged += Incomes_IsVisibleChanged;
    }

    private async void Incomes_Loaded(object sender, RoutedEventArgs e) => await StartListeningAsync();

    private void Incomes_Unloaded(object sender, RoutedEventArgs e) => StopListening();

    private async void Incomes_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
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
        if (dataStale)
            await RefreshAsync();
        else
            classification.Refresh(Range.StartDate, Range.EndDate, force: false);
    }

    private void StopListening()
    {
        if (comparisonRequest is not null)
            dataStale = true;
        revealGeneration++;
        listeningForChanges = false;
        CancelRefresh();
        context.IsLoading = false;
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        classification.Clear();
        InvalidateCategoryNames();
        incomeConnectionRevision++;
        notificationTarget = null;
        expandedGroups.Clear();
        CancelRefresh();
        dataStale = true;
        renderedQuery = null;
        context.Incomes = [];
        context.HasLoaded = false;
        context.Status = "";
        context.SetComparison(null, settings.DisplayCurrency);
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

    private void CancelRefresh()
    {
        comparisonRequest?.Cancel();
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

    internal void InvalidateCategoryNames() => categoryNames = null;

    private Task RefreshAsync() => RefreshAsync(Range.StartDate, Range.EndDate);

    private async Task RefreshAsync(DateTime start, DateTime finish)
    {
        CancelRefresh();
        dataStale = true;
        if (!SelectorRangeDatesElement.HasCompleteRange)
        {
            classification.Clear();
            renderedQuery = null;
            UpdateRows([]);
            context.HasLoaded = false;
            context.IsLoading = false;
            context.Status = "";
            context.SetComparison(null, settings.DisplayCurrency);
            return;
        }
        if (start > finish)
        {
            context.Status = "Дата начала позже даты окончания.";
            context.IsLoading = false;
            return;
        }
        classification.Refresh(start, finish);
        using var request = new CancellationTokenSource();
        activeRequest = request;
        var target = settings.DisplayCurrency;
        var query = (start, finish, target, settings.DisplayTimeZoneId);
        context.IsLoading = renderedQuery != query;
        context.SummaryCurrency = target;
        if (renderedQuery != query)
        {
            context.HasLoaded = false;
            context.SetComparison(null, target);
        }
        try
        {
            var previousAvatars = context.Incomes.Select(row => row.Income.User)
                .Where(user => user.ImageRaw is { Length: > 0 }).DistinctBy(user => user.Id)
                .ToDictionary(user => user.Id, user => user.ImageRaw!);
            var incomes = await server.GetIncomesAsync(start, finish, previousAvatars, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            var rates = await exchangeRates.GetAsync(incomes.Select(income =>
                (income.Currency, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn))), target, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            if (categoryNames is null && incomes.Any(income => income.CategoryId.HasValue))
            {
                var categories = await server.GetOperationCategoriesAsync(BudgetEvents.Income, request.Token);
                if (request != activeRequest || refreshPending)
                    return;
                categoryNames = categories?.ToDictionary(category => category.Id);
            }
            var rows = incomes.Select(income => CreateRow(income, target, rates) with
            {
                Category = income.CategoryId is { } categoryId
                    ? new IncomeCategoryGroup(categoryId, categoryNames?.GetValueOrDefault(categoryId))
                    : IncomeCategoryGroup.Uncategorized
            }).ToArray();
            dataStale = false;
            renderedQuery = query;
            UpdateRows(rows);
            context.HasLoaded = true;
            SelectNotificationTarget();
            notificationTarget = null;
            var currencyStatus = rows.Any(row => !HistoricalExchangeRates.IsCurrencyCode(row.Income.Currency))
                ? "У части доходов не указана валюта." : "";
            var rateStatus = rows.Any(row => row.AggregateAmount is null && HistoricalExchangeRates.IsCurrencyCode(row.Income.Currency))
                ? "Для части доходов нет курса." : "";
            context.Status = string.Join(' ', new[] { currencyStatus, rateStatus }.Where(status => status.Length > 0));
            _ = RefreshComparisonAsync(rows, start, finish, target);
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == activeRequest && !refreshPending)
            {
                if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                {
                    renderedQuery = null;
                    context.Incomes = [];
                    context.HasLoaded = false;
                }
                if (renderedQuery != query)
                    context.Status = CastarServerClient.DescribeError(error);
            }
        }
        finally
        {
            if (request == activeRequest)
            {
                activeRequest = null;
                if (refreshPending)
                    _ = RefreshAsync();
                else
                    context.IsLoading = false;
            }
        }
    }

    private async Task RefreshComparisonAsync(IncomeDisplayRow[] rows, DateTime start, DateTime finish, string target)
    {
        comparisonRequest?.Cancel();
        using var request = new CancellationTokenSource();
        comparisonRequest = request;
        context.IsComparisonLoading = true;
        try
        {
            if (!HistoricalExchangeRates.IsCurrencyCode(target) || rows.Any(row => row.AggregateAmount is null))
            {
                context.SetComparison(null, target, "Для сравнения нужны валюта и курсы");
                return;
            }
            if (PeriodComparison.PreviousPeriod(start, finish) is not { } previous)
            {
                context.SetComparison(null, target, "Нет предыдущего периода");
                return;
            }

            // Reuse the same HTTP, date/time and historical FX pipeline as the visible period.
            // The comparison loads independently and never delays the list or its current total.
            var incomes = await server.GetIncomeAmountsAsync(previous.StartDate, previous.EndDate, request.Token);
            var rates = await exchangeRates.GetAsync(incomes.Select(income =>
                (income.Currency, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn))), target, request.Token);
            if (request.IsCancellationRequested || comparisonRequest != request)
                return;

            var previousRows = incomes.Select(income => CreateRow(income, target, rates)).ToArray();
            if (previousRows.Any(row => row.AggregateAmount is null))
                context.SetComparison(null, target, "Для предыдущего периода нет валюты или курса");
            else
                context.SetComparison(rows.Sum(row => row.AggregateAmount!.Value) -
                    previousRows.Sum(row => row.AggregateAmount!.Value), target);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error) || error is OverflowException)
        {
            if (comparisonRequest == request && !request.IsCancellationRequested)
                context.SetComparison(null, target, "Не удалось сравнить периоды");
        }
        finally
        {
            if (comparisonRequest == request)
            {
                comparisonRequest = null;
                context.IsComparisonLoading = false;
            }
        }
    }

    private IncomeDisplayRow CreateRow(Income income, string target,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        var displayDate = BudgetTime.InZone(income.AddedOn, settings.DisplayTimeZone);
        if (HistoricalExchangeRates.IsCurrencyCode(income.Currency))
        {
            if (target.Length == 0 || income.Currency == target)
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), income.Sum, income.Currency, displayDate);
            if (rates.TryGetValue((income.Currency!, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn)), out var rate))
            {
                var converted = HistoricalExchangeRates.ConvertAmount(income.Sum, rate);
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency, converted, target), converted, target, displayDate);
            }
        }

        return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), null, null, displayDate);
    }

    private void UpdateRows(IncomeDisplayRow[] rows)
    {
        var previous = context.Incomes;
        if (previous.Count == rows.Length && previous.Zip(rows).All(pair => SameRow(pair.First, pair.Second)))
            return;

        var selectedIds = IncomesListView.SelectedItems.Cast<IncomeDisplayRow>().Select(row => row.Income.Id).ToHashSet();
        applyingIncomes = true;
        try
        {
            if (previous is ObservableCollection<IncomeDisplayRow> current && current.Count == rows.Length
                && current.Zip(rows).All(pair => pair.First.Income.Id == pair.Second.Income.Id))
            {
                for (var index = 0; index < rows.Length; index++)
                    if (!SameRow(current[index], rows[index]))
                        current[index] = rows[index];
                context.OnPropertyChanged(nameof(IncomesContext.TotalSum));
            }
            else
            {
                context.Incomes = new ObservableCollection<IncomeDisplayRow>(rows);
            }

            if (selectedIds.Count > 0)
                foreach (var row in IncomesListView.Items.OfType<IncomeDisplayRow>().Where(row => selectedIds.Contains(row.Income.Id)))
                    if (!IncomesListView.SelectedItems.Contains(row))
                        IncomesListView.SelectedItems.Add(row);
        }
        finally
        {
            applyingIncomes = false;
            UpdateIncomeSelection();
        }
    }

    internal void RevealNotification(Guid id, DateTime? addedOn)
    {
        SetUncategorizedFilter(false);
        notificationTarget = id;
        if (!SelectorRangeDatesElement.IncludeOperationDate(addedOn) && !SelectNotificationTarget())
            _ = RefreshAfterServerChangeAsync();
    }

    private bool SelectNotificationTarget()
    {
        if (notificationTarget is not { } id || context.Incomes.FirstOrDefault(row => row.Income.Id == id) is not { } selected)
            return false;
        notificationTarget = null;
        IncomeSearchBox.Clear();
        expandedGroups.Add(selected.Category.Id);
        IncomesListView.UpdateLayout();
        foreach (var expander in VisualChildren<Expander>(IncomesListView))
            if (expander.DataContext is CollectionViewGroup { Name: IncomeCategoryGroup group } && group.Id == selected.Category.Id)
                expander.IsExpanded = true;
        IncomesListView.SelectedItem = null;
        IncomesListView.SelectedItem = selected;
        IncomesListView.ScrollIntoView(selected);
        var reveal = ++revealGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RevealLoadedIncome(selected, reveal)));
        return true;
    }

    private void RevealLoadedIncome(IncomeDisplayRow selected, long generation)
    {
        if (generation != revealGeneration || !listeningForChanges || !IsVisible ||
            IncomesListView.SelectedItem is not IncomeDisplayRow current || current.Income.Id != selected.Income.Id)
            return;

        var index = IncomesListView.Items.IndexOf(current);
        if (index < 0)
            return;
        VisualChildren<VirtualizingStackPanel>(IncomesListView).FirstOrDefault()?.BringIndexIntoViewPublic(index);
        IncomesListView.UpdateLayout();
        foreach (var expander in VisualChildren<Expander>(IncomesListView))
            if (expander.DataContext is CollectionViewGroup { Name: IncomeCategoryGroup group } && group.Id == current.Category.Id)
                expander.IsExpanded = true;
        IncomesListView.UpdateLayout();
        IncomesListView.ScrollIntoView(current);
        (IncomesListView.ItemContainerGenerator.ContainerFromItem(current) as FrameworkElement)?.BringIntoView();
    }

    private static bool SameRow(IncomeDisplayRow previous, IncomeDisplayRow next)
    {
        var first = previous.Income;
        var second = next.Income;
        return first.Id == second.Id && first.Sum == second.Sum && first.Currency == second.Currency
            && previous.Category == next.Category && first.CategoryState == second.CategoryState
            && first.AddedOn == second.AddedOn && first.Source == second.Source && first.UserId == second.UserId
            && first.User.Name == second.User.Name && first.User.Sex == second.User.Sex
            && first.User.ColorRaw.AsSpan().SequenceEqual(second.User.ColorRaw)
            && first.User.ImageRaw.AsSpan().SequenceEqual(second.User.ImageRaw)
            && previous.Amount == next.Amount && previous.DisplayDate == next.DisplayDate
            && previous.AggregateAmount == next.AggregateAmount && previous.AggregateCurrency == next.AggregateCurrency;
    }

    private async void SelectorRangeDates_DateRangeChanged(object? sender, EventArgs e)
    {
        dataStale = true;
        if (!SelectorRangeDatesElement.HasCompleteRange && !listeningForChanges)
            classification.Clear();
        if (listeningForChanges && e is RangeDate range)
            await RefreshAsync(range.StartDate, range.EndDate);
    }

    private async void IncomesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.Handled)
            return;
        if (e.OriginalSource is DependencyObject original && IsActionElement(original))
            return;
        if (sender is ListViewItem { IsSelected: true, DataContext: IncomeDisplayRow row })
            await EditIncomeRowAsync(row);
    }

    private Task EditIncomeRowAsync(IncomeDisplayRow row, object? source = null)
    {
        var draft = row.Income.Clone<Income>();
        draft.AddedOn = row.DisplayDate;
        return EditIncomeAsync(draft, row.Income.AddedOn, source);
    }

    private async void EditIncomeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetIncomeMenuRow(sender) is { } row)
            await EditIncomeRowAsync(row, sender);
    }

    private async void AddNewIncomeButton_Click(object sender, RoutedEventArgs e) =>
        await EditIncomeAsync(new Income { AddedOn = BudgetTime.InZone(DateTime.UtcNow, settings.DisplayTimeZone) }, source: sender);

    private async Task EditIncomeAsync(Income income, DateTime? instant = null, object? source = null)
    {
        if (editingIncome)
            return;
        editingIncome = true;
        try
        {
            var revision = incomeConnectionRevision;
            var initialDate = income.AddedOn;
            var isNew = income.Id == Guid.Empty;
            IncomePanel? editor = null;
            await ButtonLoading.RunAsync(source, async () =>
            {
                var currencyTask = LoadManualCurrencyAsync();
                var usersTask = server.GetUsersAsync();
                var categoriesTask = server.GetOperationCategoriesAsync(BudgetEvents.Income);
                await Task.WhenAll(currencyTask, usersTask, categoriesTask);
                if (revision != incomeConnectionRevision)
                    return;
                var currency = await currencyTask;
                if (isNew && currency is not null)
                    income.Currency = currency.DefaultCurrency;
                editor = new IncomePanel(income, currency?.Currencies, await categoriesTask) { Users = await usersTask };
            });
            if (editor is null || revision != incomeConnectionRevision)
                return;

            var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                options.SuccessButton.SuccessConfirmButtonText = isNew ? "Добавить" : "Сохранить";
                options.AllowMissClick = false;
                options.Title = isNew ? "Новый доход" : "Изменить доход";
                options.EditorIcon = editor.EditorIcon;
                return options;
            }, () => ValidateIncome(income, editor));
            if (accepted != true || revision != incomeConnectionRevision)
                return;

            await ButtonLoading.RunAsync(source, async () =>
            {
                if (isNew)
                    await server.AddIncomeAsync(income, editor.CategoryChange);
                else
                    await server.UpdateIncomeAsync(income, income.AddedOn == initialDate ? instant : null, editor.CategoryChange);
                await RefreshAfterServerChangeAsync();
            });
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
        finally
        {
            editingIncome = false;
        }
    }

    private async Task<ManualCurrencySettingsDto?> LoadManualCurrencyAsync()
    {
        try
        {
            return (await server.GetServerSettingsAsync()).ManualCurrency;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ValidatePopupDialog ValidateIncome(Income income, IncomePanel editor)
    {
        if (income.User is null)
            return new ValidatePopupDialog(false, "Выберите пользователя.");
        if (string.IsNullOrWhiteSpace(income.Source))
            return new ValidatePopupDialog(false, "Укажите источник.");
        if (!editor.ValidateAmount() || income.Sum == 0)
            return new ValidatePopupDialog(false, "Укажите ненулевую сумму.");
        if (income.Id == Guid.Empty && !HistoricalExchangeRates.IsCurrencyCode(income.Currency))
            return new ValidatePopupDialog(false, "Выберите валюту.");
        if (!editor.ValidateDate() || income.AddedOn == default)
            return new ValidatePopupDialog(false, "Введите дату и время в формате ДД.ММ.ГГГГ ЧЧ:ММ.");
        return new ValidatePopupDialog(true, null);
    }

    private async void DeleteIncomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetIncomeMenuRow(sender) is not { } row)
            return;

        var income = row.Income;
        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        var description = $"{income.Source} — {MoneyText.Format(income.Sum, income.Currency)}";
        var accepted = await PopupDialog.Show(description, PopupDialog.TypeDialogEnum.Confirm,
            options =>
            {
                options.Title = "Удалить доход";
                options.SuccessButton.SuccessConfirmButtonText = "Удалить";
                options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                return options;
            });
        if (accepted != true || connection != (settings.ServerUrl, settings.ProtectedApiKey))
            return;

        try
        {
            await ButtonLoading.RunAsync(sender, async () =>
            {
                await server.DeleteIncomeAsync(income.Id);
                await RefreshAfterServerChangeAsync();
            });
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
            await RefreshAfterServerChangeAsync();
        }
    }

    private void Incomes_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void IncomeActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IncomeDisplayRow row, ContextMenu: { } menu } button)
            return;
        menu.Tag = new IncomeMenuSnapshot(row, incomeConnectionRevision);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private sealed record IncomeMenuSnapshot(IncomeDisplayRow Row, long ConnectionRevision);

    private void IncomeSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!applyingIncomes && ReferenceEquals(e.Source, sender))
            UpdateIncomeSelection();
    }

    private void UpdateIncomeSelection() =>
        SelectionAmount.Show(SelectionTotal, SelectionTotalText, IncomesListView.SelectedItems
            .OfType<IncomeDisplayRow>().Select(row => (row.AggregateAmount, row.AggregateCurrency)));

    private IncomeDisplayRow? GetIncomeMenuRow(object sender)
    {
        if (sender is not MenuItem { Parent: ContextMenu { Tag: IncomeMenuSnapshot snapshot } menu })
            return null;
        menu.IsOpen = false;
        return snapshot.ConnectionRevision == incomeConnectionRevision ? snapshot.Row : null;
    }

    private void ClearIncomeSearch_Click(object sender, RoutedEventArgs e)
    {
        IncomeSearchBox.Clear();
        IncomeSearchBox.Focus();
    }

    private void IncomeSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        incomeSearch = (sender as TextBox)?.Text.Trim() ?? "";
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = incomeSearch.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshIncomeFilter();
    }

    internal void ShowUncategorized()
    {
        IncomeSearchBox.Clear();
        expandedGroups.Add(Guid.Empty);
        SetUncategorizedFilter(true);
        IncomesListView.UpdateLayout();
        foreach (var expander in VisualChildren<Expander>(IncomesListView))
            RestoreGroupState(expander);
    }

    private void ClearUncategorizedFilter_Click(object sender, RoutedEventArgs e) => SetUncategorizedFilter(false);

    private void SetUncategorizedFilter(bool enabled)
    {
        if (uncategorizedOnly == enabled)
            return;

        uncategorizedOnly = enabled;
        UncategorizedFilterButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshIncomeFilter();
    }

    private void RefreshIncomeFilter()
    {
        if (Resources["IncomesSRC"] is CollectionViewSource { View: { } view })
            view.Refresh();
        if (EmptyIncomeText is not null)
            EmptyIncomeText.Text = incomeSearch.Length != 0 ? "Ничего не найдено"
                : uncategorizedOnly ? "Нет доходов без категории" : "Нет доходов";
    }

    private void Incomes_Filter(object sender, FilterEventArgs e)
    {
        if (e.Item is not IncomeDisplayRow row)
            return;
        e.Accepted = (!uncategorizedOnly || row.Income.CategoryId is null) &&
            (incomeSearch.Length == 0 || new[] { row.Income.Source, row.Category.Label, row.Income.User.Name, row.Amount.Primary, row.Amount.Original,
                row.DisplayDate.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) }
            .Any(value => value?.Contains(incomeSearch, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    private void IncomeGroup_Loaded(object sender, RoutedEventArgs e) => RestoreGroupState((Expander)sender);

    private void IncomeGroup_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RestoreGroupState((Expander)sender);

    private void RestoreGroupState(Expander expander) =>
        expander.SetCurrentValue(Expander.IsExpandedProperty,
            expander.DataContext is CollectionViewGroup { Name: IncomeCategoryGroup group } && expandedGroups.Contains(group.Id));

    private void IncomeGroup_Expanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Expander { DataContext: CollectionViewGroup { Name: IncomeCategoryGroup group } })
            expandedGroups.Add(group.Id);
    }

    private void IncomeGroup_Collapsed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Expander { DataContext: CollectionViewGroup { Name: IncomeCategoryGroup group } })
            expandedGroups.Remove(group.Id);
    }

    private static bool IsActionElement(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase)
                return true;
        return false;
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }

    private sealed class IncomesContext : PeriodComparison
    {
        private bool isLoading;
        private bool hasLoaded;
        private string summaryCurrency = "";
        private string status = "";
        private IReadOnlyList<IncomeDisplayRow> incomes = [];

        public bool IsLoading { get => isLoading; set => SetField(ref isLoading, value); }
        public string Status { get => status; set => SetField(ref status, value); }
        public bool HasLoaded
        {
            get => hasLoaded;
            set { if (SetField(ref hasLoaded, value)) OnPropertyChanged(nameof(TotalSum)); }
        }
        public string TotalSum => !hasLoaded ? "—" : Incomes.Count == 0 ? MoneyText.Format(0, summaryCurrency) :
            IncomeDisplayRow.FormatTotals(Incomes) is { Length: > 0 } total ? total : "—";
        public string SummaryCurrency
        {
            get => summaryCurrency;
            set { if (SetField(ref summaryCurrency, value)) OnPropertyChanged(nameof(TotalSum)); }
        }
        public IReadOnlyList<IncomeDisplayRow> Incomes
        {
            get => incomes;
            set
            {
                if (SetField(ref incomes, value))
                    OnPropertyChanged(nameof(TotalSum));
            }
        }
    }
}

internal sealed record IncomeDisplayRow(
    Income Income, MoneyAmountDisplay Amount, decimal? AggregateAmount, string? AggregateCurrency, DateTime DisplayDate = default)
{
    public IncomeCategoryGroup Category { get; init; } = IncomeCategoryGroup.Uncategorized;
    internal static string FormatTotals(IEnumerable<IncomeDisplayRow> rows) => MoneyText.FormatTotals(rows
        .Where(row => row.AggregateAmount.HasValue).Select(row => (row.AggregateAmount!.Value, row.AggregateCurrency)));
}

internal sealed record IncomeCategoryGroup(Guid Id, BudgetCategoryDto? Category)
{
    internal static IncomeCategoryGroup Uncategorized { get; } = new(Guid.Empty, null);
    public string Label => Category?.Name ?? (Id == Guid.Empty ? "Без категории" : "Категория недоступна");
    public string? IconKey => Category?.IconKey;
    public override string ToString() => Label;
}

public sealed class IncomeGroupPresentationConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter?.ToString() == "prefix")
            return value is decimal amount && amount > 0 ? "+" : "";
        var count = value is int number ? number : 0;
        var remainder = count % 100;
        var suffix = remainder is >= 11 and <= 14 ? "записей" : (count % 10) switch { 1 => "запись", >= 2 and <= 4 => "записи", _ => "записей" };
        return $"{count} {suffix}";
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var rows = (values.FirstOrDefault() as IEnumerable)?.OfType<IncomeDisplayRow>().ToArray() ?? [];
        if (parameter?.ToString() == "warning")
            return rows.Any(row => row.AggregateAmount is null) ? Visibility.Visible : Visibility.Collapsed;
        var total = IncomeDisplayRow.FormatTotals(rows);
        return total.Length == 0 ? "—" : total;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
