using Castar.Compontents;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.Painting.ImageFilters;
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
    private DateOnly? currentBalanceDate;
    private Task? currentBalanceTask;
    private CancellationTokenSource? currentBalanceRequest;
    private int currentBalanceVersion;
    private string? currentBalanceError;
    private StatisticsCurrency? renderedRemaining;
    private StatisticsCurrency? renderedMonthly;
    private string? renderedBurnKey;
    private Action<Chart>? updateBurnStroke;
    private string[] currencyCodes = [];
    private string? preferredCurrency;
    private bool updatingCurrencyPicker;
    private bool updatingBurnPicker;
    private MonthChoice? selectedBurnMonth;
    private static readonly KeyValuePair<int, string>[] BurnHistoryPeriods =
        [new(3, "3 месяца"), new(6, "6 месяцев"), new(12, "12 месяцев")];
    private int burnHistoryMonths = 3;
    private int reserveHistoryMonths = 3;
    public string ReserveHistorySelection => reserveHistoryMonths.ToString(CultureInfo.InvariantCulture);
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
        BurnHistoryPicker.ItemsSource = BurnHistoryPeriods;
        BurnHistoryPicker.SelectedValue = burnHistoryMonths;
        remainingChart = CreateChart("Динамика остатка средств");
        monthlyChart = CreateChart("Доходы и расходы по месяцам", true);
        burnChart = CreateChart("Скорость расходования бюджета", true);
        burnChart.UpdateStarted += chart => updateBurnStroke?.Invoke(chart.CoreChart);
        HeatPanel.Plot.Height = double.NaN;
        server.ConnectionChanged += async (_, _) => { statistics.InvalidateCache(); InvalidateCurrentBalance(); await ResetAsync(); };
        settings.PropertyChanged += Settings_PropertyChanged;
    }

    private StatisticsChartCard[] Cards => [RemainingPanel, BurnPanel, MonthlyPanel, HeatPanel];
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
        else await EnsureCurrentBalanceAsync();
    }
    private void Stop()
    {
        listeningForChanges = false;
        if (burnRequest is not null) dataStale = true;
        if (currentBalanceRequest is not null) InvalidateCurrentBalance();
        CancelRefresh();
        foreach (var card in Cards) card.Stop();
    }
    private void Charts_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RemainingColumn is null) return;
        var wide = BudgetContent.ActualWidth >= 1100;
        Totals.Tag = wide ? 5 : 3;
        RemainingColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(BurnPanel, wide ? 1 : 0);
        Grid.SetRow(BurnPanel, wide ? 0 : 1);
        Grid.SetRow(MonthlyPanel, wide ? 1 : 2);
        Grid.SetColumn(HeatPanel, wide ? 1 : 0);
        Grid.SetRow(HeatPanel, wide ? 1 : 3);
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
        renderedRemaining = renderedMonthly = null;
        renderedBurnKey = null;
        DrawPeriodTotals();
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
        var asOf = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, settings.DisplayTimeZone).DateTime);
        if (currentBalanceDate != today)
        {
            InvalidateCurrentBalance();
            currentBalanceDate = today;
        }
        if (currentBalanceTask is not null && currentBalanceError is null) return currentBalanceTask;
        currentBalanceError = null;
        var request = new CancellationTokenSource();
        currentBalanceRequest = request;
        DrawTotals();
        return currentBalanceTask = LoadCurrentBalanceAsync(asOf, request, currentBalanceVersion);
    }

    private async Task LoadCurrentBalanceAsync(DateTimeOffset asOf, CancellationTokenSource request, int version)
    {
        try
        {
            var result = await statistics.LoadCurrentAsync(asOf, settings.DisplayTimeZone,
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

    private void DrawTotals() => DrawTotals(reserveOnly: false);

    private void DrawTotals(bool reserveOnly)
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
        var reserve = FinancialReserve(current, amount, code);
        if (reserveOnly && Totals.Items[^1] is StatisticsTotal existing)
        {
            existing.UpdateReserve(reserve);
            return;
        }

        Dashboard.Visibility = Visibility.Visible;
        Totals.ItemsSource = new[]
        {
            FormatTotal("Доходы", period?.Income, period?.Currency ?? code),
            FormatTotal("Расходы", period?.Expenses, period?.Currency ?? code),
            FormatTotal("Итог за период", period?.Balance, period?.Currency ?? code) with
            {
                Sign = Math.Sign(period?.Balance ?? 0m)
            },
            new StatisticsTotal("Текущий остаток", amount is { } balance
                ? HistoricalExchangeRates.Format(decimal.Round(balance, 2, MidpointRounding.AwayFromZero), code)
                : "—", warning, currentBalanceRequest is not null),
            reserve
        };
    }

    private void ReserveHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: StatisticsTotal { HasReserveSelector: true }, SelectedValue: string value }
            || !int.TryParse(value, CultureInfo.InvariantCulture, out var months)
            || months is not (3 or 6 or BudgetStatistics.MaxReserveHistoryMonths)
            || reserveHistoryMonths == months)
            return;

        reserveHistoryMonths = months;
        DrawTotals(reserveOnly: true);
    }

    private StatisticsTotal FinancialReserve(StatisticsCurrency? current, decimal? balance, string currency)
    {
        var tile = new StatisticsTotal("Финансовая подушка", "—", currentBalanceError ?? "",
            currentBalanceRequest is not null) { HasReserveSelector = true };
        if (currentBalanceSnapshot is not { } data)
            return tile;
        if (!HistoricalExchangeRates.IsCurrencyCode(settings.DisplayCurrency))
            return tile with { Warning = "Выберите валюту отображения." };
        var today = currentBalanceDate!.Value;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var historyStart = monthStart.AddMonths(-reserveHistoryMonths);
        if (data.EarliestOperationDate is not { } earliest || earliest > historyStart)
            return tile with { Warning = $"Недостаточно истории за {reserveHistoryMonths} полных месяцев." };
        if (current?.RemainingPoints.LastOrDefault() is not { } last)
            return tile with { Warning = $"Нет расходов за {reserveHistoryMonths} полных месяцев." };

        var history = current.DailyPoints.Where(point => point.Date >= historyStart && point.Date < monthStart).ToArray();
        if (history.Any(point => point.Expenses is null))
            return tile with { Warning = "Для расчёта не хватает валюты или курса расходов." };

        var average = history.Sum(point => point.Expenses.GetValueOrDefault()) / reserveHistoryMonths;
        if (average <= 0)
            return tile with { Warning = "Нет положительных среднемесячных расходов." };

        tile = tile with
        {
            AverageMonthlyExpenses = HistoricalExchangeRates.Format(
                decimal.Round(average, 2, MidpointRounding.AwayFromZero), currency)
        };
        if (balance is null)
            return tile with { Warning = "Текущий остаток недоступен." };

        var months = (double)Math.Max(balance.Value, 0m) / (double)average;
        var days = Math.Ceiling(months * history.Length / reserveHistoryMonths);
        return tile with
        {
            Amount = $"{months:N1} мес.",
            EstimatedDepletion = days <= DateOnly.MaxValue.DayNumber - last.Date.DayNumber
                ? last.Date.AddDays((int)days).ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)
                : null
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
        periodTask = LoadPeriodAsync(range, currency, request.Token);
        var remainingTask = statistics.LoadAsync(range.StartDate, range.EndDate, currency, request.Token);
        var period = periodTask;
        var tasks = new[]
        {
            MonthlyPanel.LoadAsync(period, _ => DrawMonthly(), request.Token),
            HeatPanel.LoadAsync(period, _ => DrawHeatmap(), request.Token),
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
    private async Task<StatisticsSnapshot> LoadPeriodAsync(RangeDate range, string currency, CancellationToken token)
    {
        var result = await statistics.LoadPeriodAsync(range.StartDate, range.EndDate, currency, token);
        token.ThrowIfCancellationRequested();
        snapshot = result;
        UpdateCurrencyPicker();
        DrawPeriodTotals();
        return result;
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
            renderedRemaining = null;
            DrawTotals();
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
        renderedRemaining = null;
        DrawTotals(); DrawMonthly(); DrawHeatmap(); DrawRemainingSelected(); DrawBurn();
    }
    private void DrawPeriodTotals()
    {
        DrawTotals();
        MissingAmounts.Visibility = snapshot is { } data && data.UnconvertedIncomes + data.UnconvertedExpenses > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is { } result)
            MissingAmounts.Text = $"Вне итогов из-за отсутствия валюты или курса — доходы ({result.UnconvertedIncomes}), расходы ({result.UnconvertedExpenses}).";
    }
    private static bool SameCurrency(StatisticsCurrency? old, StatisticsCurrency current) => old is not null
        && old.Currency == current.Currency && old.Income == current.Income && old.Expenses == current.Expenses && old.Balance == current.Balance
        && old.RemainingWarning == current.RemainingWarning
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
        var stroke = Paint("Button.Foreground.Green", 2);
        var minimum = values.Min(point => point?.Y) ?? 0;
        remainingChart.Series = [new LineSeries<ObservablePoint?> { Name = "Остаток", Values = values,
            Fill = new LinearGradientPaint(stroke.Color.WithAlpha(64), stroke.Color.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            // Keep the fill below the line even for a flat negative balance. LiveCharts clips it to the plot.
            Pivot = Math.Min(0, minimum * 2),
            Stroke = stroke, GeometrySize = 0, EnableNullSplitting = true, LineSmoothness = 0,
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
        await LoadBurnAsync(periodTask, activeRequest?.Token ?? CancellationToken.None);
    }

    private async void BurnHistoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BurnHistoryPicker.SelectedValue is not int months || months == burnHistoryMonths) return;
        burnHistoryMonths = months;
        if (periodTask is not null && listeningForChanges)
            await LoadBurnAsync(periodTask, activeRequest?.Token ?? CancellationToken.None);
    }

    private async Task LoadBurnAsync(Task<StatisticsSnapshot> period, CancellationToken token)
    {
        burnRequest?.Cancel();
        baselineSnapshot = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        burnRequest = request;
        var month = selectedBurnMonth?.Month;
        if (month is null) { burnRequest = null; return; }
        var historyMonths = burnHistoryMonths;
        if ((month.Value.Year - 1) * 12 + month.Value.Month <= historyMonths)
        {
            BurnPanel.Reset();
            renderedBurnKey = null;
            BurnPanel.Message("Недостаточно предыдущих месяцев.");
            burnRequest = null;
            return;
        }
        var start = month.Value.AddMonths(-historyMonths).ToDateTime(TimeOnly.MinValue);
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
        currency ??= new StatisticsCurrency(SelectedCurrency, 0, 0, 0)
        {
            DailyPoints = Enumerable.Range(1, DateTime.DaysInMonth(month.Value.Year, month.Value.Month))
                .Select(day => new StatisticsDailyPoint(new DateOnly(month.Value.Year, month.Value.Month, day), 0,
                    burnSnapshot?.UnconvertedExpenses > 0 ? null : 0)).ToArray()
        };
        var history = baselineSnapshot.Currencies.FirstOrDefault(row => row.Currency == currency.Currency);
        var selected = currency.DailyPoints.Where(point => point.Date.Year == month.Value.Year && point.Date.Month == month.Value.Month).ToArray();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, settings.DisplayTimeZone).DateTime);
        decimal? spent = 0;
        var actual = selected.Select(point => { spent = spent.HasValue && point.Expenses.HasValue ? spent.Value + point.Expenses : null;
            return point.Date > today ? null : spent; }).ToArray();
        var baseline = history is null && baselineSnapshot.UnconvertedExpenses > 0
            ? new decimal?[selected.Length]
            : BudgetStatistics.AverageAccumulatedExpenses(month.Value, burnHistoryMonths, history?.DailyPoints ?? []);
        BurnPanel.Message(baseline.Any(amount => amount is null) ? "Для среднего не хватает валюты или курса."
            : selected.Any(point => point.Date <= today && !point.Expenses.HasValue) ? "Часть расходов недоступна." : "");
        var key = $"{currency.Currency}/{month}/{burnHistoryMonths}/{string.Join("|", actual)}/{string.Join("|", baseline)}";
        if (key == renderedBurnKey) return;
        renderedBurnKey = key;
        Attach(BurnPanel, burnChart);
        burnChart.XAxes = [new Axis { Labels = selected.Select(point => point.Date.ToString("dd.MM")).ToArray(), MinStep = 1, LabelsPaint = Paint("Foreground.Gray") }];
        burnChart.YAxes = [AmountAxis()];
        var historyLabel = BurnHistoryPeriods.First(option => option.Key == burnHistoryMonths).Value;
        var expected = DrawLine($"Среднее за {historyLabel}", "Foreground.Gray", baseline, currency.Currency, roundToCents: true);
        expected.Stroke = new SolidColorPaint(((SolidColorPaint)expected.Stroke!).Color, 2) { PathEffect = new DashEffect([5, 5]) };
        burnChart.Series = [DrawActualExpenses(actual, baseline, currency.Currency), expected];
    }
    private void DrawHeatmap()
    {
        var currency = PeriodCurrency;
        if (currency is null || currency.DailyPoints.Count == 0)
        {
            HeatPanel.Plot.Children.Clear();
            HeatPanel.Message("Нет операций за период.");
            return;
        }
        HeatPanel.Message("");
        var start = DateOnly.FromDateTime(Dates.RangeDate.StartDate);
        var end = DateOnly.FromDateTime(Dates.RangeDate.EndDate);
        var byMonth = (start.Year != 9999 || start.Month != 12) && end >= start.AddMonths(1);
        var cells = byMonth
            ? currency.DailyPoints.GroupBy(day => new DateOnly(day.Date.Year, day.Date.Month, 1))
                .Select(month => new ExpenseHeatmapCell(month.Key,
                    month.Any(day => day.Expenses is null) ? null : month.Sum(day => day.Expenses!.Value))).ToArray()
            : currency.DailyPoints.Select(day => new ExpenseHeatmapCell(day.Date, day.Expenses)).ToArray();
        var maximum = cells.Select(cell => Math.Abs(cell.Amount.GetValueOrDefault())).Max();
        var periods = cells.GroupBy(cell => new DateOnly(cell.Date.Year, byMonth ? 1 : cell.Date.Month, 1))
            .Select(group => new ExpenseHeatmapPeriod(group.Key, currency.Currency, byMonth, group.ToArray(),
                maximum, day => ExpenseDayRequested?.Invoke(day))).ToArray();
        if (HeatPanel.Plot.Children.OfType<ListBox>().FirstOrDefault() is { } previous &&
            previous.ItemsSource is ExpenseHeatmapPeriod[] old && old.Length == periods.Length &&
            old.Zip(periods).All(pair => pair.First.Start == pair.Second.Start && pair.First.Currency == pair.Second.Currency
                && pair.First.ByMonth == pair.Second.ByMonth && pair.First.Maximum == pair.Second.Maximum
                && pair.First.Cells.SequenceEqual(pair.Second.Cells))) return;
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemsSource = periods };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        var template = new DataTemplate();
        template.VisualTree = new FrameworkElementFactory(typeof(ExpenseHeatmapCalendarView));
        list.ItemTemplate = template;
        HeatPanel.Plot.Children.Clear(); HeatPanel.Plot.Children.Add(list);
    }
    private static StatisticsTotal FormatTotal(string label, decimal? amount, string currency) =>
        new(label, amount is { } value ? HistoricalExchangeRates.Format(value, currency) : "Недоступно");
    private LineSeries<decimal?> DrawActualExpenses(decimal?[] actual, decimal?[] baseline, string currency)
    {
        var accent = Paint("Highlight").Color;
        var blue = new SKColor(87, 134, 255);
        var series = DrawLine("Расходы", "Highlight", actual, currency, roundToCents: true);
        series.Fill = new LinearGradientPaint(
            [accent.WithAlpha(32), blue.WithAlpha(16), blue.WithAlpha(0)],
            new SKPoint(0, 0), new SKPoint(0.2f, 1), [0, 0.4f, 1]);
        series.Pivot = Math.Min(0, 2 * (double)(actual.Min() ?? 0m));
        var (colors, positions) = ExpenseComparisonStops(actual, baseline, accent);
        series.Stroke = ExpenseStroke(colors, positions, accent, 0, 1);
        updateBurnStroke = null;
        if (colors.Distinct().Skip(1).Any())
        {
            var previousStart = 0f;
            var previousEnd = 1f;
            updateBurnStroke = core =>
            {
                var width = core.DrawMarginSize.Width;
                if (width <= 0)
                    return;

                var left = core.DrawMarginLocation.X;
                var start = (float)((burnChart.ScaleDataToPixels(new LvcPointD(0, 0)).X - left) / width);
                var end = (float)((burnChart.ScaleDataToPixels(new LvcPointD(actual.Length - 1, 0)).X - left) / width);
                if (!float.IsFinite(start) || !float.IsFinite(end) || start == end
                    || (Math.Abs(start - previousStart) < 0.001f && Math.Abs(end - previousEnd) < 0.001f))
                    return;

                previousStart = start;
                previousEnd = end;
                series.Stroke = ExpenseStroke(colors, positions, accent, start, end);
            };
        }
        series.GeometrySize = 6;
        series.GeometryFill = Paint("Foreground.White");
        series.GeometryStroke = new LinearGradientPaint(accent, blue)
        {
            StrokeThickness = 1.5f,
            ImageFilter = new DropShadow(0, 0, 2, 2, blue.WithAlpha(120))
        };

        var firstPoint = Array.FindIndex(actual, amount => amount.HasValue);
        var lastPoint = Array.FindLastIndex(actual, amount => amount.HasValue);
        series.PointMeasured += point =>
        {
            if (point.Visual is not { } marker)
                return;

            var isLastPoint = point.Index == lastPoint;
            marker.Opacity = isLastPoint || point.Index == firstPoint || (point.Index + 1) % 5 == 0 ? 1 : 0;
            if (!isLastPoint)
                return;

            const float finalSize = 10;
            marker.Width = finalSize;
            marker.Height = finalSize;
            marker.TranslateTransform = new LvcPoint(-finalSize / 2, -finalSize / 2);
        };
        return series;
    }

    private static LinearGradientPaint ExpenseStroke(SKColor[] colors, float[] positions, SKColor accent, float start, float end) =>
        new(colors, new SKPoint(start, 0.5f), new SKPoint(end, 0.5f), positions)
        {
            StrokeThickness = 3,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            ImageFilter = new DropShadow(0, 0, 2.5f, 2.5f, accent.WithAlpha(82))
        };

    private static (SKColor[] Colors, float[] Positions) ExpenseComparisonStops(decimal?[] actual, decimal?[] baseline, SKColor neutral)
    {
        var above = new SKColor(241, 126, 141);
        var below = new SKColor(103, 221, 162);
        var differences = actual.Select((value, index) => value is { } spent && baseline[index] is { } average
            ? (double)spent - (double)average
            : 0).ToArray();
        var scale = differences.Select(Math.Abs).DefaultIfEmpty().Max();
        if (scale == 0)
            return ([neutral, neutral], [0, 1]);
        if (differences.Length == 1)
        {
            var color = Color(differences[0]);
            return ([color, color], [0, 1]);
        }

        var colors = new List<SKColor>();
        var positions = new List<float>();
        var previous = differences[0];
        Add(0, Color(previous));
        for (var index = 1; index < differences.Length; index++)
        {
            var current = differences[index];
            if (previous != 0 && current != 0 && Math.Sign(previous) != Math.Sign(current))
            {
                var crossing = Math.Abs(previous) / (Math.Abs(previous) + Math.Abs(current));
                Add(index - 1 + crossing, neutral);
            }
            Add(index, Color(current));
            previous = current;
        }
        return ([.. colors], [.. positions]);

        SKColor Color(double difference)
        {
            var intensity = Math.Abs(difference) / scale;
            var target = difference > 0 ? above : below;
            return new SKColor(
                Blend(neutral.Red, target.Red, intensity),
                Blend(neutral.Green, target.Green, intensity),
                Blend(neutral.Blue, target.Blue, intensity));
        }

        static byte Blend(byte from, byte to, double fraction) => (byte)Math.Round(from + (to - from) * fraction);
        void Add(double index, SKColor color)
        {
            positions.Add((float)(index / (differences.Length - 1)));
            colors.Add(color);
        }
    }

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
    private sealed record StatisticsTotal(string Label, string Amount, string Warning = "", bool IsLoading = false)
        : INotifyPropertyChanged
    {
        public string Amount { get; set; } = Amount;
        public string Warning { get; set; } = Warning;
        public int Sign { get; init; }
        public bool HasReserveSelector { get; init; }
        public string? AverageMonthlyExpenses { get; set; }
        public string? EstimatedDepletion { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void UpdateReserve(StatisticsTotal source)
        {
            Amount = source.Amount;
            Warning = source.Warning;
            AverageMonthlyExpenses = source.AverageMonthlyExpenses;
            EstimatedDepletion = source.EstimatedDepletion;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
