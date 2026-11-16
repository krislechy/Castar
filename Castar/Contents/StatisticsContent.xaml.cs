using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
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
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace Castar.Contents;

public partial class StatisticsContent : UserControl
{
    private const int VisibleCategoryCount = 8;
    private readonly BudgetStatistics statistics;
    private readonly ClientSettings settings;
    private readonly CartesianChart timelineChart;
    private readonly CartesianChart categoriesChart;
    private bool listeningForChanges;
    private bool dataStale = true;
    private CancellationTokenSource? activeRequest;
    private bool refreshPending;
    private StatisticsSnapshot? snapshot;
    private StatisticsCurrency? renderedCurrency;
    private string[] currencyCodes = [];
    private string? preferredCurrency;
    private bool updatingCurrencyPicker;

    internal StatisticsContent(BudgetStatistics statistics, CastarServerClient server, ClientSettings settings)
    {
        this.statistics = statistics;
        this.settings = settings;
        InitializeComponent();

        timelineChart = CreateChart("График доходов и расходов за период");
        timelineChart.LegendPosition = LegendPosition.Top;
        TimelinePanel.ObjectContent = timelineChart;
        categoriesChart = CreateChart("График расходов по категориям");
        CategoriesPanel.ObjectContent = categoriesChart;
        server.ConnectionChanged += async (_, _) =>
        {
            ShowSnapshot(null);
            await ResetAsync();
        };
        settings.PropertyChanged += Settings_PropertyChanged;
    }

    private async void Statistics_Loaded(object sender, RoutedEventArgs e) => await StartAsync();

    private void Statistics_Unloaded(object sender, RoutedEventArgs e) => Stop();

    private async void Statistics_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
            await StartAsync();
        else
            Stop();
    }

    private async Task StartAsync()
    {
        if (!IsVisible || listeningForChanges)
            return;

        listeningForChanges = true;
        if (dataStale)
            await RefreshAsync();
    }

    private void Stop()
    {
        listeningForChanges = false;
        CancelRefresh();
        Progress.IsLoading = false;
    }

    private async void Dates_DateRangeChanged(object sender, EventArgs e) => await ResetAsync();

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientSettings.DisplayCurrency) or nameof(ClientSettings.DisplayTimeZoneId))
            await ResetAsync();
    }

    private Task ResetAsync()
    {
        dataStale = true;
        if (!listeningForChanges)
            return Task.CompletedTask;

        CancelRefresh();
        ShowSnapshot(null);
        return RefreshAsync();
    }

    private void CancelRefresh()
    {
        refreshPending = false;
        var request = activeRequest;
        activeRequest = null;
        request?.Cancel();
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
        Progress.IsLoading = snapshot is null;
        try
        {
            var result = await statistics.LoadAsync(range.StartDate, range.EndDate, settings.DisplayCurrency, request.Token);
            if (request == activeRequest && !refreshPending)
            {
                dataStale = false;
                ShowSnapshot(result);
            }
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == activeRequest && !refreshPending)
            {
                ShowSnapshot(null);
                ShowStatus(CastarServerClient.DescribeError(error));
            }
        }
        finally
        {
            if (request == activeRequest)
            {
                activeRequest = null;
                Progress.IsLoading = false;
                if (refreshPending)
                {
                    refreshPending = false;
                    _ = RefreshAsync();
                }
            }
        }
    }

    private void ShowSnapshot(StatisticsSnapshot? result)
    {
        snapshot = result;
        var codes = result?.Currencies.Select(currency => currency.Currency).ToArray() ?? [];
        updatingCurrencyPicker = true;
        if (!codes.SequenceEqual(currencyCodes))
        {
            currencyCodes = codes;
            CurrencyPicker.ItemsSource = codes;
        }

        var currency = codes.Contains(preferredCurrency) ? preferredCurrency : codes.FirstOrDefault();
        CurrencyPicker.SelectedItem = currency;
        CurrencyPicker.Visibility = codes.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        updatingCurrencyPicker = false;

        ShowMissingAmounts(result);
        ShowStatus(result switch
        {
            { HasRecords: false } => "Нет операций за период.",
            { Currencies.Count: 0 } => "Итоги недоступны.",
            _ => ""
        });
        ShowCurrency(result?.Currencies.FirstOrDefault(row => row.Currency == currency));
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMissingAmounts(StatisticsSnapshot? result)
    {
        if (result is null || result.UnconvertedIncomes + result.UnconvertedExpenses == 0)
        {
            MissingAmounts.Visibility = Visibility.Collapsed;
            return;
        }

        MissingAmounts.Text = $"Вне итогов из-за отсутствия валюты или курса — доходы ({result.UnconvertedIncomes}), расходы ({result.UnconvertedExpenses}).";
        MissingAmounts.Visibility = Visibility.Visible;
    }

    private void CurrencyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCurrencyPicker || CurrencyPicker.SelectedItem is not string currency)
            return;

        preferredCurrency = currency;
        ShowCurrency(snapshot?.Currencies.FirstOrDefault(row => row.Currency == currency));
    }

    private void ShowCurrency(StatisticsCurrency? currency)
    {
        if (currency == renderedCurrency || currency is not null && renderedCurrency is { } previous &&
            (previous with { Points = currency.Points, Categories = currency.Categories }) == currency &&
            previous.Points.SequenceEqual(currency.Points) && previous.Categories.SequenceEqual(currency.Categories))
            return;

        renderedCurrency = currency;
        Dashboard.Visibility = currency is null ? Visibility.Collapsed : Visibility.Visible;
        if (currency is null)
        {
            Totals.ItemsSource = null;
            timelineChart.Series = [];
            categoriesChart.Series = [];
            return;
        }

        Totals.ItemsSource = new[]
        {
            FormatTotal("Доходы", currency.Income, currency.Currency),
            FormatTotal("Расходы", currency.Expenses, currency.Currency),
            FormatTotal("Разница", currency.Balance, currency.Currency)
        };
        DrawTimeline(currency);
        DrawCategories(currency);
    }

    private void DrawTimeline(StatisticsCurrency currency)
    {
        var points = currency.Points;
        TimelinePanel.Visibility = points.Any(point => point.Income.HasValue || point.Expenses.HasValue)
            ? Visibility.Visible : Visibility.Collapsed;
        timelineChart.XAxes =
        [
            new Axis { Labels = points.Select(point => point.Label).ToArray(), MinStep = 1, LabelsPaint = Paint("Foreground.Gray") }
        ];
        timelineChart.YAxes = [AmountAxis()];
        timelineChart.Series =
        [
            DrawLine("Доходы", "Button.Foreground.Green", points.Select(point => point.Income), currency.Currency),
            DrawLine("Расходы", "Highlight", points.Select(point => point.Expenses), currency.Currency)
        ];
    }

    private void DrawCategories(StatisticsCurrency currency)
    {
        CategoriesPanel.Visibility = currency.Categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (currency.Categories.Count == 0)
        {
            categoriesChart.Series = [];
            return;
        }

        var ordered = currency.Categories.OrderByDescending(category => decimal.Abs(category.Amount)).ToArray();
        var groups = ordered.Take(VisibleCategoryCount)
            .Select(category => (Label: $"Категория {category.Id}", category.Amount)).ToList();
        if (ordered.Length > VisibleCategoryCount)
            groups.Add(("Остальные", ordered.Skip(VisibleCategoryCount).Sum(category => category.Amount)));
        groups.Reverse();

        var amountAxis = AmountAxis();
        if (groups.All(group => group.Amount >= 0))
            amountAxis.MinLimit = 0;
        else if (groups.All(group => group.Amount <= 0))
            amountAxis.MaxLimit = 0;
        categoriesChart.XAxes = [amountAxis];
        categoriesChart.YAxes =
        [
            new Axis { Labels = groups.Select(group => group.Label).ToArray(), MinStep = 1, LabelsPaint = Paint("Foreground.Gray") }
        ];
        categoriesChart.Series =
        [
            new RowSeries<decimal>
            {
                Values = groups.Select(group => group.Amount).ToArray(),
                Name = "Расходы",
                Fill = Paint("Highlight"),
                XToolTipLabelFormatter = point => groups[point.Index].Label,
                YToolTipLabelFormatter = point => HistoricalExchangeRates.Format(point.Model, currency.Currency)
            }
        ];
    }

    private static StatisticsTotal FormatTotal(string label, decimal? amount, string currency) =>
        new(label, amount is { } value ? HistoricalExchangeRates.Format(value, currency) : "Недоступно");

    private LineSeries<decimal?> DrawLine(string name, string brush, IEnumerable<decimal?> values, string currency) =>
        new()
        {
            Values = values.ToArray(),
            Name = name,
            Stroke = Paint(brush, 2),
            Fill = null,
            GeometryFill = Paint(brush),
            GeometryStroke = null,
            GeometrySize = 5,
            LineSmoothness = 0,
            YToolTipLabelFormatter = point => point.Model is { } amount ? HistoricalExchangeRates.Format(amount, currency) : ""
        };

    private Axis AmountAxis() => new()
    {
        LabelsPaint = Paint("Foreground.Gray"),
        SeparatorsPaint = Paint("Main.Background.lvl3", 1),
        Labeler = FormatAmountLabel
    };

    private static string FormatAmountLabel(double value)
    {
        var magnitude = Math.Abs(value);
        var culture = CultureInfo.CurrentCulture;
        if (magnitude >= 1_000_000)
            return (value / 1_000_000).ToString("0.##", culture) + "M";
        if (magnitude >= 1_000)
            return (value / 1_000).ToString("0.##", culture) + "k";
        return value.ToString("0.####", culture);
    }

    private CartesianChart CreateChart(string name)
    {
        var chart = new CartesianChart
        {
            LegendTextPaint = Paint("Foreground.White"),
            TooltipTextPaint = Paint("Foreground.White"),
            TooltipBackgroundPaint = Paint("Main.Background.lvl3")
        };
        chart.SetResourceReference(Control.BackgroundProperty, "Main.Background.lvl1");
        AutomationProperties.SetName(chart, name);
        return chart;
    }

    private SolidColorPaint Paint(string resource, float stroke = 0)
    {
        var color = ((SolidColorBrush)FindResource(resource)).Color;
        return new SolidColorPaint(new SKColor(color.R, color.G, color.B, color.A)) { StrokeThickness = stroke };
    }

    private sealed record StatisticsTotal(string Label, string Amount);
}
