using Castar.Api;
using Castar.Currency;
using Castar.ServerConnection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Statistics;

internal sealed record StatisticsSnapshot(bool HasRecords, int UnconvertedIncomes, int UnconvertedExpenses,
    IReadOnlyList<StatisticsCurrency> Currencies);

internal sealed record StatisticsCurrency(string Currency, decimal? Income, decimal? Expenses, decimal? Balance,
    IReadOnlyList<StatisticsPoint> Points, IReadOnlyList<StatisticsCategory> Categories);

internal sealed record StatisticsPoint(string Label, decimal? Income, decimal? Expenses);

internal sealed record StatisticsCategory(int Id, decimal Amount);

internal sealed class BudgetStatistics(CastarServerClient server, HistoricalExchangeRates exchangeRates)
{
    internal async Task<StatisticsSnapshot> LoadAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token)
    {
        var incomes = server.GetIncomeRowsAsync(start, finish, token);
        var purchases = server.GetPurchasesAsync(start, finish, token);
        await Task.WhenAll(incomes, purchases).ConfigureAwait(false);

        var entries = (await incomes).Select(income =>
        {
            var date = DateOnly.FromDateTime(server.DisplayOperationDate(income.AddedOn, income.AddedOnUtc)!.Value);
            return new Entry(true, income.Currency, income.Sum, date, income.RateDate ?? DateOnly.FromDateTime(income.AddedOn), []);
        }).Concat((await purchases).Select(purchase =>
        {
            var date = purchase.AddedOn is { } added ? DateOnly.FromDateTime(added) : purchase.ReceiptDate;
            return new Entry(false, purchase.Currency, purchase.TotalSum, date,
                purchase.RateDate ?? purchase.ReceiptDate ?? date, purchase.Items);
        })).ToArray();
        var rates = await exchangeRates.GetAsync(entries.Where(entry => entry.RateDate.HasValue)
            .Select(entry => (entry.Currency, entry.RateDate!.Value)), displayCurrency, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Calculate(entries, DateOnly.FromDateTime(start), DateOnly.FromDateTime(finish), displayCurrency, rates);
    }

    private static StatisticsSnapshot Calculate(Entry[] entries, DateOnly start, DateOnly finish, string displayCurrency,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        var converted = entries.Select(entry => Convert(entry, displayCurrency, rates)).ToArray();
        var omitted = converted.Where(entry => entry.Currency is null).ToArray();
        var convertedMode = displayCurrency.Length > 0;
        var buckets = PeriodBuckets(start, finish).ToArray();
        var currencies = converted.Where(entry => entry.Currency is not null)
            .GroupBy(entry => entry.Currency!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Summarize(group.Key, group.ToArray(), convertedMode ? omitted : [], buckets))
            .ToArray();
        return new StatisticsSnapshot(entries.Length != 0, omitted.Count(entry => entry.Original.IsIncome),
            omitted.Count(entry => !entry.Original.IsIncome), currencies);
    }

    private static ConvertedEntry Convert(Entry entry, string displayCurrency,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        if (!HistoricalExchangeRates.IsCurrencyCode(entry.Currency))
            return new ConvertedEntry(entry, null, null);
        if (displayCurrency.Length == 0 || displayCurrency == entry.Currency)
            return new ConvertedEntry(entry, entry.Currency, null);
        if (entry.RateDate is { } date && rates.TryGetValue((entry.Currency!, date), out var rate))
            return new ConvertedEntry(entry, displayCurrency, rate);
        return new ConvertedEntry(entry, null, null);
    }

    private static StatisticsCurrency Summarize(string currency, ConvertedEntry[] converted, ConvertedEntry[] omitted,
        PeriodBucket[] buckets)
    {
        var all = converted.Concat(omitted).ToArray();
        var income = Total(all, isIncome: true);
        var expenses = Total(all, isIncome: false);
        var byDate = all.Where(entry => entry.Original.ReportDate.HasValue)
            .OrderBy(entry => entry.Original.ReportDate).ToArray();
        var points = new List<StatisticsPoint>(buckets.Length);
        var first = 0;
        foreach (var bucket in buckets)
        {
            while (first < byDate.Length && byDate[first].Original.ReportDate < bucket.Start)
                first++;
            var last = first;
            while (last < byDate.Length && byDate[last].Original.ReportDate <= bucket.End)
                last++;

            var rows = byDate.AsSpan(first, last - first);
            points.Add(new StatisticsPoint(bucket.Label, Total(rows, isIncome: true), Total(rows, isIncome: false)));
            first = last;
        }

        var categories = converted.SelectMany(entry => entry.Original.Items
                .Where(item => item.Category is not null)
                .Select(item => new StatisticsCategory(item.Category!.Value, entry.ConvertAmount(item.Sum))))
            .GroupBy(item => item.Id)
            .Select(group => new StatisticsCategory(group.Key, group.Sum(item => item.Amount)))
            .OrderByDescending(item => item.Amount).ThenBy(item => item.Id).ToArray();

        return new StatisticsCurrency(currency, income, expenses, omitted.Length == 0 ? income - expenses : null,
            points, categories);
    }

    private static decimal? Total(ReadOnlySpan<ConvertedEntry> rows, bool isIncome)
    {
        var converted = false;
        var omitted = false;
        var sum = 0m;
        foreach (var row in rows)
        {
            if (row.Original.IsIncome != isIncome)
                continue;
            if (row.Currency is null)
                omitted = true;
            else
            {
                converted = true;
                sum += row.ConvertAmount(row.Original.Amount);
            }
        }
        return omitted && !converted ? null : sum;
    }

    private static IEnumerable<PeriodBucket> PeriodBuckets(DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        if (days <= 224)
        {
            var step = days <= 45 ? 1 : 7;
            for (var first = start.DayNumber; first <= end.DayNumber; first += step)
            {
                var last = Math.Min(first + step - 1, end.DayNumber);
                var beginning = DateOnly.FromDayNumber(first);
                var finish = DateOnly.FromDayNumber(last);
                yield return new PeriodBucket(beginning, finish, step == 1 ? $"{beginning:dd.MM}" : $"{beginning:dd.MM}–{finish:dd.MM}");
            }
            yield break;
        }

        var startMonth = (start.Year - 1) * 12 + start.Month - 1;
        var endMonth = (end.Year - 1) * 12 + end.Month - 1;
        var months = endMonth - startMonth + 1;
        var stepMonths = months <= 120 ? 1 : ((months + 119) / 120 + 11) / 12 * 12;
        for (var first = startMonth; first <= endMonth; first += stepMonths)
        {
            var last = Math.Min(first + stepMonths - 1, endMonth);
            var firstMonth = new DateOnly(first / 12 + 1, first % 12 + 1, 1);
            var lastYear = last / 12 + 1;
            var lastMonth = last % 12 + 1;
            var lastDate = new DateOnly(lastYear, lastMonth, DateTime.DaysInMonth(lastYear, lastMonth));
            var label = stepMonths == 1 ? $"{firstMonth:MM.yyyy}"
                : firstMonth.Year == lastYear ? $"{firstMonth.Year}" : $"{firstMonth.Year}–{lastYear}";
            yield return new PeriodBucket(firstMonth < start ? start : firstMonth, lastDate > end ? end : lastDate, label);
        }
    }

    private sealed record Entry(bool IsIncome, string? Currency, decimal Amount, DateOnly? ReportDate,
        DateOnly? RateDate, IReadOnlyList<PurchaseItemDto> Items);

    private sealed record ConvertedEntry(Entry Original, string? Currency, decimal? Rate)
    {
        public decimal ConvertAmount(decimal amount) =>
            Rate is { } rate ? HistoricalExchangeRates.ConvertAmount(amount, rate) : amount;
    }

    private sealed record PeriodBucket(DateOnly Start, DateOnly End, string Label);
}
