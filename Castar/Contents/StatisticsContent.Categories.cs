using Castar.Api;
using Castar.Compontents;
using Castar.Statistics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

public partial class StatisticsContent
{
    private CancellationTokenSource? categoryChartRequest;
    private StatisticsCategorySnapshot? categoryChartSnapshot;
    private bool categoryChartStale = true;
    private int categoryChartVersion;

    internal void RefreshCategoryChartAfterServerChange()
    {
        uncategorizedStale = true;
        uncategorizedRevision++;
        _ = EnsureUncategorizedAsync();
        categoryChartStale = true;
        categoryChartVersion++;
        EnsureCategoryChart();
    }

    private void EnsureCategoryChart()
    {
        if (!listeningForChanges || !Dates.HasCompleteRange || !categoryChartStale || categoryChartRequest is not null)
            return;

        var range = Dates.RangeDate;
        if (range.StartDate > range.EndDate)
        {
            CategoriesPanel.Card.Message("Дата начала позже даты окончания.");
            return;
        }

        var request = new CancellationTokenSource();
        categoryChartRequest = request;
        _ = LoadCategoryChartAsync(range.StartDate, range.EndDate, settings.DisplayCurrency, request);
    }

    private async Task LoadCategoryChartAsync(DateTime start, DateTime end, string currency, CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                while (!request.IsCancellationRequested)
                {
                    var version = categoryChartVersion;
                    await CategoriesPanel.Card.LoadAsync(statistics.LoadCategoriesAsync(start, end, currency, request.Token),
                        result =>
                        {
                            if (version != categoryChartVersion)
                                return;
                            categoryChartSnapshot = result;
                            DrawCategoryChart();
                        }, request.Token);
                    if (categoryChartRequest != request)
                        return;
                    if (version == categoryChartVersion)
                    {
                        categoryChartStale = !CategoriesPanel.Card.LastLoadSucceeded;
                        return;
                    }
                }
            }
            finally
            {
                if (categoryChartRequest == request)
                    categoryChartRequest = null;
            }
        }
    }

    private void CancelCategoryChart(bool clear)
    {
        var previous = categoryChartRequest;
        categoryChartRequest = null;
        if (previous is not null)
        {
            categoryChartStale = true;
            previous.Cancel();
        }
        if (clear)
        {
            categoryChartStale = true;
            categoryChartSnapshot = null;
            CategoriesPanel.Reset();
        }
        else
            CategoriesPanel.Card.Stop();
    }

    private bool CategoryChartShowsIncome => CategoriesPanel.Kind == BudgetEvents.Income;

    private Guid[] CategoryChartExclusions => CategoryChartShowsIncome
        ? settings.ExcludedIncomeChartCategories : settings.ExcludedPurchaseChartCategories;

    private void SaveCategoryChartFilter()
    {
        var exclusions = CategoriesPanel.ExcludedCategoryIds.ToArray();
        if (CategoryChartShowsIncome)
            settings.ExcludedIncomeChartCategories = exclusions;
        else
            settings.ExcludedPurchaseChartCategories = exclusions;
        ShowCategoryChartWarning();
    }

    private void DrawCategoryChart()
    {
        if (categoryChartSnapshot is not { } snapshot)
            return;

        var amounts = snapshot.Rows.Where(row => row.IsIncome == CategoryChartShowsIncome).ToArray();
        var currency = SelectedCurrency ?? (settings.DisplayCurrency.Length > 0 ? settings.DisplayCurrency
            : amounts.Select(row => row.Currency).Where(code => code.Length > 0).Order().FirstOrDefault() ?? "");
        var totals = amounts.Where(row => row.Currency == currency)
            .ToDictionary(row => row.CategoryId ?? Guid.Empty, row => row.Amount);
        var missingCurrency = amounts.Where(row => row.Currency.Length == 0 && row.UnconvertedCount > 0)
            .Select(row => row.CategoryId ?? Guid.Empty).ToHashSet();
        var catalog = snapshot.Categories.Where(category => category.Kind == CategoriesPanel.Kind)
            .ToDictionary(category => category.Id);
        foreach (var row in amounts.Where(row => row.CategoryId.HasValue).DistinctBy(row => row.CategoryId))
        {
            var id = row.CategoryId!.Value;
            if (!catalog.ContainsKey(id))
                catalog[id] = new(id, CategoriesPanel.Kind, row.CategoryName, row.IconKey);
        }
        CategoryStatisticsRow ChartRow(Guid id, string name, string? icon)
        {
            var hasAmount = totals.TryGetValue(id, out var amount);
            return new(id, name, icon, amount, hasAmount || missingCurrency.Contains(id));
        }

        var rows = catalog.Values.Select(category => ChartRow(category.Id, category.Name, category.IconKey)).ToList();
        if (amounts.Any(row => row.CategoryId is null))
            rows.Add(ChartRow(Guid.Empty, "Без категории", null));

        CategoriesPanel.ShowRows(rows, currency, CategoryChartExclusions);
        ShowCategoryChartWarning();
    }

    private void ShowCategoryChartWarning()
    {
        var warning = categoryChartSnapshot?.Rows.Any(row => row.IsIncome == CategoryChartShowsIncome
            && row.UnconvertedCount > 0 && !CategoryChartExclusions.Contains(row.CategoryId ?? Guid.Empty)) == true;
        CategoriesPanel.Card.Message(warning ? "Часть сумм без валюты или курса." : "");
    }
}
