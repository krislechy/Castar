using Castar.Api;
using Castar.Currency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Castar.Compontents;

internal sealed record CategoryStatisticsRow(Guid Id, string Name, string? IconKey, decimal? Amount, bool HasOperations = true);

public partial class CategoryStatisticsChart : UserControl
{
    private readonly HashSet<Guid> excludedIds = [];
    private readonly ContentControl plot;
    private IReadOnlyList<CategoryStatisticsRow> rows = [];
    private string currency = "";
    private bool initialized;

    public CategoryStatisticsChart()
    {
        InitializeComponent();
        plot = new ContentControl
        {
            ContentTemplate = (DataTemplate)FindResource("CategoryPlotTemplate"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        initialized = true;
    }

    internal StatisticsChartCard Card => ChartCard;
    internal string Kind => KindPicker.SelectedValue as string ?? BudgetEvents.Purchase;
    internal IReadOnlyCollection<Guid> ExcludedCategoryIds => excludedIds.ToArray();
    internal event EventHandler? KindChanged;
    internal event EventHandler? FilterChanged;

    internal void SetKind(string kind) => KindPicker.SelectedValue = kind;

    internal void ShowRows(IReadOnlyList<CategoryStatisticsRow> rows, string currency, IReadOnlyCollection<Guid>? excludedIds)
    {
        this.rows = rows;
        this.currency = currency;
        this.excludedIds.Clear();
        if (excludedIds is not null)
            this.excludedIds.UnionWith(excludedIds);
        FilterChoices.ItemsSource = rows.OrderBy(row => row.Id == Guid.Empty)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => new FilterChoice(row.Id, row.Name, !this.excludedIds.Contains(row.Id))).ToArray();
        FilterButton.IsEnabled = rows.Count > 0;
        RenderPlot();
    }

    internal void Reset()
    {
        FilterPopup.IsOpen = false;
        FilterButton.IsEnabled = false;
        FilterChoices.ItemsSource = null;
        rows = [];
        excludedIds.Clear();
        Card.Reset();
    }

    private void RenderPlot()
    {
        var visible = rows.Where(row => row.HasOperations && !excludedIds.Contains(row.Id)).OrderByDescending(row => row.Amount)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var minimum = Math.Abs(Math.Min(0, (double)(visible.Min(row => row.Amount) ?? 0)));
        var maximum = Math.Max(0, (double)(visible.Max(row => row.Amount) ?? 0));
        if (minimum == 0 && maximum == 0)
            maximum = 1;

        var isIncome = Kind == BudgetEvents.Income;
        var chartRows = visible.Select(row =>
        {
            var value = (double)(row.Amount ?? 0);
            var negative = Math.Max(0, -value);
            var positive = Math.Max(0, value);
            return new BarRow(row.Name, row.IconKey,
                row.Amount is { } amount ? MoneyText.FormatRounded(amount, currency) : "—", isIncome,
                Stars(minimum - negative), Stars(negative), Stars(positive), Stars(maximum - positive));
        }).ToArray();
        var message = "";
        if (visible.Length == 0)
            message = rows.Count == 0 ? "Нет категорий" : rows.All(row => excludedIds.Contains(row.Id))
                ? "Нет выбранных категорий" : "Нет операций за выбранный период";
        plot.Content = new PlotState(chartRows, message);
        if (!Card.Plot.Children.Contains(plot))
            Card.Plot.Children.Add(plot);
    }

    private static GridLength Stars(double value) => new(value, GridUnitType.Star);

    private void KindPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || KindPicker.SelectedValue is not string)
            return;

        FilterPopup.IsOpen = false;
        KindChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = true;

    private void FilterPopup_Opened(object? sender, EventArgs e) => FilterChoices.Focus();

    private void FilterPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        FilterPopup.IsOpen = false;
        FilterButton.Focus();
    }

    private void FilterChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FilterChoice category } checkbox)
            return;

        category.Selected = checkbox.IsChecked == true;
        if (category.Selected)
            excludedIds.Remove(category.Id);
        else
            excludedIds.Add(category.Id);
        RenderPlot();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FilterChoice(Guid id, string name, bool selected)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public bool Selected { get; set; } = selected;
    }

    private sealed record PlotState(IReadOnlyList<BarRow> Rows, string EmptyMessage);
    private sealed record BarRow(string Name, string? IconKey, string AmountText, bool IsIncome,
        GridLength BeforeNegative, GridLength Negative, GridLength Positive, GridLength AfterPositive);
}
