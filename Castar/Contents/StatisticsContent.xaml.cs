using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace Castar.Contents;

public partial class StatisticsContent : UserControl
{
    private readonly BudgetStatistics statistics;
    private readonly ClientSettings settings;
    private readonly CartesianChart timelineChart;
    private readonly CartesianChart remainingChart;
    private readonly CartesianChart monthlyChart;
    private readonly CartesianChart burnChart;
    private bool listeningForChanges;
    private bool dataStale = true;
    private CancellationTokenSource? activeRequest;
    private CancellationTokenSource? burnRequest;
    private Task? scheduledRefresh;
    private TaskCompletionSource? refreshCompletion;
    private int dataVersion;
    private int refreshGeneration;
    private StatisticsSnapshot? snapshot;
    private StatisticsSnapshot? remainingSnapshot;
    private StatisticsSnapshot? baselineSnapshot;
    private StatisticsSnapshot? currentBalanceSnapshot;
    private Task? currentBalanceTask;
    private CancellationTokenSource? currentBalanceRequest;
    private int currentBalanceVersion;
    private string? currentBalanceError;
    private StatisticsCurrency? renderedCurrency;
    private StatisticsCurrency? renderedRemaining;
    private StatisticsCurrency? renderedMonthly;
    private string? renderedBurnKey;
    private string[] currencyCodes = [];
    private string? preferredCurrency;
    private bool updatingCurrencyPicker;
    private bool updatingBurnPicker;
    private MonthChoice? selectedBurnMonth;
    private Task<StatisticsSnapshot>? periodTask;
    internal event Action<DateOnly>? ExpenseDayRequested;
    private SelectorRangeDates? periodSelector;
    private bool periodChanged;
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
        periodChanged = true;
    }

    internal StatisticsContent(BudgetStatistics statistics, CastarServerClient server, ClientSettings settings)
    {
        this.statistics = statistics;
        this.settings = settings;
        InitializeComponent();
        timelineChart = CreateChart("График доходов и расходов за период", true);
        remainingChart = CreateChart("Динамика остатка средств");
        monthlyChart = CreateChart("Доходы и расходы по месяцам", true);
        burnChart = CreateChart("Скорость расходования бюджета", true);
        HeatPanel.Plot.Height = double.NaN;
        server.ConnectionChanged += async (_, _) => { statistics.InvalidateCache(); InvalidateCurrentBalance(); await ResetAsync(); };
        settings.PropertyChanged += Settings_PropertyChanged;
    }

    private StatisticsChartCard[] Cards => [TimelinePanel, RemainingPanel, MonthlyPanel, BurnPanel, HeatPanel];
    private async void Statistics_Loaded(object sender, RoutedEventArgs e) => await StartAsync();
    private void Statistics_Unloaded(object sender, RoutedEventArgs e) => Stop();
    private async void Statistics_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible) await StartAsync();
        else Stop();
    }
    private async Task StartAsync()
    {
        if (!IsVisible || listeningForChanges || periodSelector is null) return;
        listeningForChanges = true;
        if (periodChanged)
        {
            periodChanged = false;
            await ResetAsync();
        }
        else if (dataStale) await RefreshAsync();
    }
    private void Stop()
    {
        listeningForChanges = false;
        if (currentBalanceRequest is not null) InvalidateCurrentBalance();
        CancelRefresh();
        foreach (var card in Cards) card.Stop();
    }
    private void Charts_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RemainingColumn is null) return;
        var wide = BudgetContent.ActualWidth >= 1100;
        Totals.Tag = wide ? 4 : 2;
        RemainingColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(RemainingPanel, wide ? 1 : 0);
        Grid.SetRow(RemainingPanel, wide ? 0 : 1);
        Grid.SetRow(MonthlyPanel, wide ? 1 : 2);
        Grid.SetColumn(BurnPanel, wide ? 1 : 0);
        Grid.SetRow(BurnPanel, wide ? 1 : 3);
        Grid.SetRow(HeatPanel, wide ? 2 : 4);
    }
    private async void Dates_DateRangeChanged(object? sender, EventArgs e)
    {
        dataStale = true;
        if (!listeningForChanges)
        {
            periodChanged = true;
            return;
        }
        periodChanged = false;
        await ResetAsync();
    }
    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientSettings.DisplayCurrency) or nameof(ClientSettings.DisplayTimeZoneId))
        {
            statistics.InvalidateCache();
            InvalidateCurrentBalance();
            await ResetAsync();
        }
    }
    private Task ResetAsync()
    {
        CancelRefresh();
        dataStale = true;
        snapshot = remainingSnapshot = baselineSnapshot = null;
        renderedCurrency = renderedRemaining = renderedMonthly = null;
        renderedBurnKey = null;
        DrawTotals();
        foreach (var card in Cards) card.Reset();
        return listeningForChanges ? RefreshAsync() : Task.CompletedTask;
    }
    private void CancelRefresh()
    {
        refreshGeneration++;
        scheduledRefresh = null;
        var old = activeRequest;
        activeRequest = null;
        old?.Cancel();
        burnRequest?.Cancel();
    }

    private void InvalidateCurrentBalance()
    {
        currentBalanceVersion++;
        currentBalanceRequest?.Cancel();
        currentBalanceRequest = null;
        currentBalanceTask = null;
        currentBalanceSnapshot = null;
        currentBalanceError = null;
    }

    private Task EnsureCurrentBalanceAsync()
    {
        if (currentBalanceTask is not null && currentBalanceError is null) return currentBalanceTask;
        currentBalanceError = null;
        var request = new CancellationTokenSource();
        currentBalanceRequest = request;
        DrawTotals();
        return currentBalanceTask = LoadCurrentBalanceAsync(request, currentBalanceVersion);
    }

    private async Task LoadCurrentBalanceAsync(CancellationTokenSource request, int version)
    {
        try
        {
            var result = await statistics.LoadCurrentAsync(DateTimeOffset.UtcNow, settings.DisplayTimeZone,
                settings.DisplayCurrency, request.Token);
            if (version != currentBalanceVersion || request.IsCancellationRequested) return;
            currentBalanceSnapshot = result;
            currentBalanceError = null;
            UpdateCurrencyPicker();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == currentBalanceVersion)
                currentBalanceError = CastarServerClient.IsConnectionError(error)
                    ? CastarServerClient.DescribeError(error) : "Не удалось загрузить текущий остаток.";
        }
        finally
        {
            if (currentBalanceRequest == request) currentBalanceRequest = null;
            request.Dispose();
            if (version == currentBalanceVersion) DrawTotals();
        }
    }

    private void DrawTotals()
    {
        var period = PeriodCurrency;
        var code = SelectedCurrency ?? settings.DisplayCurrency;
        var current = currentBalanceSnapshot?.Currencies.FirstOrDefault(row => row.Currency == code);
        var amount = current?.RemainingPoints.LastOrDefault()?.Amount;
        var warning = currentBalanceError ?? current?.RemainingWarning ?? "";
        if (currentBalanceSnapshot is { } complete && current is null)
        {
            if (!complete.HasRecords || complete.Currencies.Count > 0
                && complete.Currencies.All(row => row.RemainingWarning.Length == 0)
                && complete.UnconvertedIncomes + complete.UnconvertedExpenses == 0)
                amount = 0m;
            else
                warning = complete.Currencies.Select(row => row.RemainingWarning).FirstOrDefault(text => text.Length > 0)
                    ?? "Начальный остаток недоступен: в истории отсутствует валюта или курс.";
        }
        Dashboard.Visibility = Visibility.Visible;
        Totals.ItemsSource = new[]
        {
            FormatTotal("Доходы", period?.Income, period?.Currency ?? code),
            FormatTotal("Расходы", period?.Expenses, period?.Currency ?? code),
            FormatTotal("Разница", period?.Balance, period?.Currency ?? code),
            new StatisticsTotal("Текущий остаток", amount is { } balance
                ? HistoricalExchangeRates.Format(balance, code) : "—", warning, currentBalanceRequest is not null)
        };
    }
    internal Task RefreshAfterServerChangeAsync()
    {
        statistics.InvalidateCache();
        InvalidateCurrentBalance();
        dataStale = true;
        dataVersion++;
        if (!listeningForChanges) return Task.CompletedTask;
        if (scheduledRefresh is not null) return scheduledRefresh;
        var generation = refreshGeneration;
        scheduledRefresh = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                while (listeningForChanges && generation == refreshGeneration)
                {
                    // A financial event invalidates the result, not the running HTTP request.
                    // Let independent cards finish, then merge every pending event into one reload.
                    if (refreshCompletion is { } running) await running.Task;
                    if (!listeningForChanges || generation != refreshGeneration) return;
                    var version = dataVersion;
                    await RefreshAsync();
                    if (version == dataVersion) return;
                }
            }
            finally
            {
                if (generation == refreshGeneration) scheduledRefresh = null;
            }
        }, DispatcherPriority.Background).Task.Unwrap();
        return scheduledRefresh;
    }
    private async Task RefreshAsync()
    {
        if (activeRequest is not null && refreshCompletion is { } running)
        {
            await running.Task;
            return;
        }
        var currentBalance = EnsureCurrentBalanceAsync();
        var range = Dates.RangeDate;
        if (range.StartDate > range.EndDate)
        {
            foreach (var card in Cards) card.Message("Дата начала позже даты окончания.");
            await currentBalance;
            return;
        }
        using var request = new CancellationTokenSource();
        activeRequest = request;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshCompletion = completion;
        var version = dataVersion;
        dataStale = true;
        UpdateBurnMonths();
        var currency = settings.DisplayCurrency;
        periodTask = statistics.LoadPeriodAsync(range.StartDate, range.EndDate, currency, request.Token);
        var remainingTask = statistics.LoadAsync(range.StartDate, range.EndDate, currency, request.Token);
        var period = periodTask;
        var tasks = new[]
        {
            TimelinePanel.LoadAsync(period, result => { snapshot = result; UpdateCurrencyPicker(); DrawPeriod(); }, request.Token),
            MonthlyPanel.LoadAsync(period, result => { snapshot = result; UpdateCurrencyPicker(); DrawMonthly(); }, request.Token),
            HeatPanel.LoadAsync(period, result => { snapshot = result; UpdateCurrencyPicker(); DrawHeatmap(); }, request.Token),
            RemainingPanel.LoadAsync(remainingTask, result => { remainingSnapshot = result; UpdateCurrencyPicker(); DrawRemainingSelected(); }, request.Token),
            LoadBurnAsync(period, request.Token),
            currentBalance
        };
        try
        {
            await Task.WhenAll(tasks);
            if (activeRequest == request)
                dataStale = version != dataVersion || currentBalanceError is not null || Cards.Any(card => !card.LastLoadSucceeded);
        }
        finally
        {
            if (activeRequest == request)
            {
                activeRequest = null;
            }
            if (refreshCompletion == completion) refreshCompletion = null;
            completion.TrySetResult();
        }
    }
    private void UpdateCurrencyPicker()
    {
        var codes = (snapshot?.Currencies ?? []).Concat(remainingSnapshot?.Currencies ?? [])
            .Concat(currentBalanceSnapshot?.Currencies ?? []).Select(row => row.Currency)
            .Concat(currentBalanceSnapshot is not null && settings.DisplayCurrency.Length > 0
                ? new[] { settings.DisplayCurrency } : Array.Empty<string>()).Distinct().Order().ToArray();
        var previous = CurrencyPicker.SelectedItem as string;
        updatingCurrencyPicker = true;
        if (!codes.SequenceEqual(currencyCodes)) { currencyCodes = codes; CurrencyPicker.ItemsSource = codes; }
        var currency = codes.Contains(preferredCurrency) ? preferredCurrency : codes.FirstOrDefault();
        if (!Equals(CurrencyPicker.SelectedItem, currency)) CurrencyPicker.SelectedItem = currency;
        CurrencyPicker.Visibility = codes.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        updatingCurrencyPicker = false;
        if (previous != currency)
        {
            renderedCurrency = renderedRemaining = null;
            DrawPeriod();
            DrawMonthly();
            DrawHeatmap();
            DrawBurn();
            DrawRemainingSelected();
        }
    }
    private string? SelectedCurrency => CurrencyPicker.SelectedItem as string;
    private StatisticsCurrency? PeriodCurrency => snapshot?.Currencies.FirstOrDefault(row => row.Currency == SelectedCurrency);
    private void CurrencyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCurrencyPicker) return;
        preferredCurrency = SelectedCurrency;
        renderedCurrency = renderedRemaining = null;
        DrawPeriod(); DrawMonthly(); DrawHeatmap(); DrawRemainingSelected(); DrawBurn();
        DrawTotals();
    }
    private void DrawPeriod()
    {
        var currency = PeriodCurrency;
        DrawTotals();
        MissingAmounts.Visibility = snapshot is { } data && data.UnconvertedIncomes + data.UnconvertedExpenses > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is { } result)
            MissingAmounts.Text = $"Вне итогов из-за отсутствия валюты или курса — доходы ({result.UnconvertedIncomes}), расходы ({result.UnconvertedExpenses}).";
        if (currency is null)
        {
            timelineChart.Series = [];
            TimelinePanel.Message(snapshot?.HasRecords == true ? "Итоги недоступны." : "Нет операций за период.");
            return;
        }
        if (SameCurrency(renderedCurrency, currency)) return;
        renderedCurrency = currency;

        Attach(TimelinePanel, timelineChart);
        timelineChart.XAxes = [new Axis { Labels = currency.Points.Select(point => point.Label).ToArray(), MinStep = 1, LabelsPaint = Paint("Foreground.Gray") }];
        timelineChart.YAxes = [AmountAxis()];
        DrawTimelineSeries(currency);
    }
    private static bool SameCurrency(StatisticsCurrency? old, StatisticsCurrency current) => old is not null
        && old.Currency == current.Currency && old.Income == current.Income && old.Expenses == current.Expenses && old.Balance == current.Balance
        && old.RemainingWarning == current.RemainingWarning && old.Points.SequenceEqual(current.Points)
        && old.RemainingPoints.SequenceEqual(current.RemainingPoints) && old.DailyPoints.SequenceEqual(current.DailyPoints)
        && old.MonthlyPoints.SequenceEqual(current.MonthlyPoints);
    private void DrawRemainingSelected()
    {
        var currency = remainingSnapshot?.Currencies.FirstOrDefault(row => row.Currency == SelectedCurrency);
        if (currency is null) { RemainingPanel.Message("Нет данных об остатке."); return; }
        RemainingPanel.Message(currency.RemainingWarning);
        if (SameCurrency(renderedRemaining, currency)) return;
        renderedRemaining = currency;
        Attach(RemainingPanel, remainingChart);
        var points = currency.RemainingPoints;
        var first = points.Count > 0 ? points[0].Date : DateOnly.FromDateTime(Dates.RangeDate.StartDate);
        remainingChart.XAxes = [DayAxis(first, points.Count)];
        remainingChart.YAxes = [AmountAxis()];
        var stride = Math.Max(1, (points.Count + 1999) / 2000);
        var values = points.Select((point, index) => (point, index)).Where(item => item.index % stride == 0 || item.index == points.Count - 1
            || item.index > 0 && points[item.index - 1].Amount.HasValue != item.point.Amount.HasValue)
            .Select(item => item.point.Amount is { } amount ? new ObservablePoint(item.index, (double)amount) : null).ToArray();
        remainingChart.Series = [new LineSeries<ObservablePoint?> { Name = "Остаток", Values = values, Fill = null,
            Stroke = Paint("Button.Foreground.Green", 2), GeometrySize = 0, EnableNullSplitting = true, LineSmoothness = 0,
            XToolTipLabelFormatter = point => point.Model?.X is { } day ? first.AddDays((int)day).ToString("dd.MM.yyyy") : "",
            YToolTipLabelFormatter = point => point.Model?.X is { } day && points[(int)day].Amount is { } amount
                ? HistoricalExchangeRates.Format(amount, currency.Currency) : "" }];
    }
    private void DrawMonthly()
    {
        var currency = PeriodCurrency;
        if (currency is null) { MonthlyPanel.Message("Нет операций за период."); return; }
        if (renderedMonthly?.Currency == currency.Currency && renderedMonthly.MonthlyPoints.SequenceEqual(currency.MonthlyPoints)) return;
        renderedMonthly = currency;
        Attach(MonthlyPanel, monthlyChart);
        monthlyChart.XAxes = [new Axis { Labels = currency.MonthlyPoints.Select(point => point.Label).ToArray(), LabelsPaint = Paint("Foreground.Gray") }];
        monthlyChart.YAxes = [AmountAxis()];
        monthlyChart.Series =
        [
            Bar("Доходы", "Button.Foreground.Green", currency.MonthlyPoints.Select(point => point.Income), currency.Currency),
            Bar("Расходы", "Highlight", currency.MonthlyPoints.Select(point => point.Expenses), currency.Currency)
        ];
    }
    private ColumnSeries<decimal?> Bar(string title, string color, IEnumerable<decimal?> values, string currency) =>
        new() { Name = title, Values = values.ToArray(), Fill = Paint(color), Stroke = null,
            YToolTipLabelFormatter = point => point.Model is { } amount ? HistoricalExchangeRates.Format(amount, currency) : "" };
    private void UpdateBurnMonths()
    {
        var start = new DateOnly(Dates.RangeDate.StartDate.Year, Dates.RangeDate.StartDate.Month, 1);
        var last = new DateOnly(Dates.RangeDate.EndDate.Year, Dates.RangeDate.EndDate.Month, 1);
        var months = new List<MonthChoice>();
        for (var month = start; month <= last;)
        {
            months.Add(new MonthChoice(month));
            if (month.Year == 9999 && month.Month == 12) break;
            month = month.AddMonths(1);
        }
        updatingBurnPicker = true;
        var previous = selectedBurnMonth;
        BurnMonthPicker.ItemsSource = months;
        selectedBurnMonth = months.FirstOrDefault(month => month == previous) ?? months.LastOrDefault();
        BurnMonthPicker.SelectedItem = selectedBurnMonth;
        BurnMonthPicker.Visibility = months.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        updatingBurnPicker = false;
    }
    private async void BurnMonthPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingBurnPicker || periodTask is null || !listeningForChanges) return;
        selectedBurnMonth = BurnMonthPicker.SelectedItem as MonthChoice;
        baselineSnapshot = null;
        await LoadBurnAsync(periodTask, activeRequest?.Token ?? CancellationToken.None);
    }
    private async Task LoadBurnAsync(Task<StatisticsSnapshot> period, CancellationToken token)
    {
        burnRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        burnRequest = request;
        var month = selectedBurnMonth?.Month;
        if (month is null) { burnRequest = null; return; }
        if (month.Value.Year == 1 && month.Value.Month <= 3)
        {
            BurnPanel.Message("Недостаточно предыдущих месяцев.");
            burnRequest = null;
            return;
        }
        var start = month.Value.AddMonths(-3).ToDateTime(TimeOnly.MinValue);
        var finish = month.Value.ToDateTime(TimeOnly.MinValue).AddTicks(-1);
        var history = statistics.LoadPeriodAsync(start, finish, settings.DisplayCurrency, request.Token);
        var monthStart = month.Value.ToDateTime(TimeOnly.MinValue);
        var monthFinish = month.Value.AddDays(DateTime.DaysInMonth(month.Value.Year, month.Value.Month) - 1)
            .ToDateTime(TimeOnly.MaxValue);
        var currentMonth = Dates.RangeDate.StartDate.Date <= monthStart && Dates.RangeDate.EndDate.Date >= monthFinish.Date
            ? period : statistics.LoadPeriodAsync(monthStart, monthFinish, settings.DisplayCurrency, request.Token);
        try
        {
            await BurnPanel.LoadAsync(CombineAsync(), _ => DrawBurn(), request.Token);
        }
        finally
        {
            if (burnRequest == request) burnRequest = null;
        }
        async Task<bool> CombineAsync()
        {
            await Task.WhenAll(currentMonth, history);
            var current = await currentMonth;
            var previous = await history;
            request.Token.ThrowIfCancellationRequested();
            burnSnapshot = current;
            baselineSnapshot = previous;
            return true;
        }
    }
    private StatisticsSnapshot? burnSnapshot;
    private void DrawBurn()
    {
        var currency = burnSnapshot?.Currencies.FirstOrDefault(row => row.Currency == SelectedCurrency);
        var month = selectedBurnMonth?.Month;
        if (month is null || baselineSnapshot is null || SelectedCurrency is null) return;
        currency ??= new StatisticsCurrency(SelectedCurrency, 0, 0, 0, [])
        {
            DailyPoints = Enumerable.Range(1, DateTime.DaysInMonth(month.Value.Year, month.Value.Month))
                .Select(day => new StatisticsDailyPoint(new DateOnly(month.Value.Year, month.Value.Month, day), 0,
                    burnSnapshot?.UnconvertedExpenses > 0 ? null : 0)).ToArray()
        };
        var history = baselineSnapshot.Currencies.FirstOrDefault(row => row.Currency == currency.Currency);
        var daysInMonth = DateTime.DaysInMonth(month.Value.Year, month.Value.Month);
        var selected = currency.DailyPoints.Where(point => point.Date.Year == month.Value.Year && point.Date.Month == month.Value.Month).ToArray();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, settings.DisplayTimeZone).DateTime);
        var missingHistory = baselineSnapshot.UnconvertedExpenses > 0 || history?.DailyPoints.Any(point => !point.Expenses.HasValue) == true;
        var average = history?.Expenses is { } amount ? amount / 3m : 0m;
        decimal? spent = 0;
        var actual = selected.Select(point => { spent = spent.HasValue && point.Expenses.HasValue ? spent.Value + point.Expenses : null;
            return point.Date > today ? null : spent; }).ToArray();
        var baseline = selected.Select(point => missingHistory ? (decimal?)null : average * point.Date.Day / daysInMonth).ToArray();
        var key = currency.Currency + month + string.Join("|", actual) + "/" + string.Join("|", baseline);
        if (key == renderedBurnKey) return;
        renderedBurnKey = key;
        Attach(BurnPanel, burnChart);
        burnChart.XAxes = [new Axis { Labels = selected.Select(point => point.Date.ToString("dd.MM")).ToArray(), MinStep = 1, LabelsPaint = Paint("Foreground.Gray") }];
        burnChart.YAxes = [AmountAxis()];
        var expected = DrawLine("Среднее за 3 месяца", "Foreground.Gray", baseline, currency.Currency, roundToCents: true);
        expected.Stroke = new SolidColorPaint(((SolidColorPaint)expected.Stroke!).Color, 2) { PathEffect = new DashEffect([5, 5]) };
        burnChart.Series = [DrawLine("Расходы", "Highlight", actual, currency.Currency, roundToCents: true), expected];
        BurnPanel.Message(missingHistory ? "Для среднего не хватает валюты или курса."
            : selected.Any(point => point.Date <= today && !point.Expenses.HasValue) ? "Часть расходов недоступна." : "");
    }
    private void DrawHeatmap()
    {
        var currency = PeriodCurrency;
        if (currency is null) { HeatPanel.Message("Нет операций за период."); return; }
        var lastDate = Dates.RangeDate.EndDate;
        var months = currency.DailyPoints.Where(day => day.Date.Year == lastDate.Year && day.Date.Month == lastDate.Month)
            .GroupBy(day => new DateOnly(day.Date.Year, day.Date.Month, 1))
            .Select(group => new ExpenseHeatmapMonth(group.Key, currency.Currency, group.ToArray(),
                day => ExpenseDayRequested?.Invoke(day))).ToArray();
        if (HeatPanel.Plot.Children.OfType<ListBox>().FirstOrDefault() is { } previous &&
            previous.ItemsSource is ExpenseHeatmapMonth[] old && old.Length == months.Length &&
            old.Zip(months).All(pair => pair.First.Month == pair.Second.Month && pair.First.Currency == pair.Second.Currency
                && pair.First.Days.SequenceEqual(pair.Second.Days))) return;
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemsSource = months };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        var template = new DataTemplate();
        template.VisualTree = new FrameworkElementFactory(typeof(ExpenseHeatmapMonthView));
        list.ItemTemplate = template;
        HeatPanel.Plot.Children.Clear(); HeatPanel.Plot.Children.Add(list);
    }
    private void TimelineSeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (renderedCurrency is { } currency) DrawTimelineSeries(currency);
    }
    private void DrawTimelineSeries(StatisticsCurrency currency)
    {
        var series = new List<ISeries>(2);
        if (TimelineSeriesPicker.SelectedItem != TimelineExpensesOnly)
            series.Add(DrawLine("Доходы", "Button.Foreground.Green", currency.Points.Select(point => point.Income), currency.Currency));
        if (TimelineSeriesPicker.SelectedItem != TimelineIncomesOnly)
            series.Add(DrawLine("Расходы", "Highlight", currency.Points.Select(point => point.Expenses), currency.Currency));
        timelineChart.Series = series;
    }
    private static StatisticsTotal FormatTotal(string label, decimal? amount, string currency) =>
        new(label, amount is { } value ? HistoricalExchangeRates.Format(value, currency) : "Недоступно");
    private LineSeries<decimal?> DrawLine(string name, string brush, IEnumerable<decimal?> values, string currency,
        bool roundToCents = false) => new()
    {
        Name = name, Values = values.ToArray(), Stroke = Paint(brush, 2), Fill = null,
        GeometrySize = 0, LineSmoothness = 0, EnableNullSplitting = true,
        YToolTipLabelFormatter = point => point.Model is { } amount
            ? HistoricalExchangeRates.Format(roundToCents ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero) : amount, currency) : ""
    };
    private Axis DayAxis(DateOnly first, int count) => new() { MinLimit = -0.5, MaxLimit = Math.Max(0.5, count - 0.5), MinStep = 1,
        Labeler = day => day >= 0 && day < count ? first.AddDays((int)Math.Round(day)).ToString("dd.MM.yy") : "",
        LabelsPaint = Paint("Foreground.Gray") };
    private Axis AmountAxis() => new() { LabelsPaint = Paint("Foreground.Gray"), SeparatorsPaint = Paint("Main.Background.lvl3", 1),
        Labeler = StatisticsAxisLabels.Amount };
    private CartesianChart CreateChart(string name, bool legend = false)
    {
        var chart = new CartesianChart { LegendPosition = legend ? LegendPosition.Top : LegendPosition.Hidden,
            LegendTextPaint = Paint("Foreground.White"), TooltipTextPaint = Paint("Foreground.White"),
            TooltipBackgroundPaint = Paint("Main.Background.lvl3") };
        chart.SetResourceReference(Control.BackgroundProperty, "Main.Background.lvl1");
        AutomationProperties.SetName(chart, name);
        return chart;
    }
    private static void Attach(StatisticsChartCard card, CartesianChart chart)
    {
        if (card.Plot.Children.Contains(chart)) return;
        card.Plot.Children.Clear(); card.Plot.Children.Add(chart);
    }
    private SolidColorPaint Paint(string resource, float stroke = 0)
    {
        var color = ((SolidColorBrush)FindResource(resource)).Color;
        return new SolidColorPaint(new SKColor(color.R, color.G, color.B, color.A)) { StrokeThickness = stroke };
    }
    private sealed record MonthChoice(DateOnly Month)
    {
        public string Label => Month.ToString("MM.yyyy", CultureInfo.InvariantCulture);
    }
    private sealed record StatisticsTotal(string Label, string Amount, string Warning = "", bool IsLoading = false);
}
