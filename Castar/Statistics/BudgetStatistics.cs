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
    IReadOnlyList<StatisticsPoint> Points)
{
    public IReadOnlyList<StatisticsRemainingPoint> RemainingPoints { get; init; } = [];
    public string RemainingWarning { get; init; } = "";
    public IReadOnlyList<StatisticsDailyPoint> DailyPoints { get; init; } = [];
    public IReadOnlyList<StatisticsPoint> MonthlyPoints { get; init; } = [];
}

internal sealed record StatisticsPoint(string Label, decimal? Income, decimal? Expenses);
internal sealed record StatisticsRemainingPoint(DateOnly Date, decimal? Amount);
internal sealed record StatisticsDailyPoint(DateOnly Date, decimal? Income, decimal? Expenses);

internal sealed class BudgetStatistics(CastarServerClient server, HistoricalExchangeRates exchangeRates)
{
    private readonly StatisticsSnapshotCache cache = new();

    internal void InvalidateCache() => cache.Invalidate();

    internal Task<StatisticsSnapshot> LoadAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token) => LoadCachedAsync(start, finish, displayCurrency, token, includeOpening: true);

    internal Task<StatisticsSnapshot> LoadPeriodAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token) => LoadCachedAsync(start, finish, displayCurrency, token, includeOpening: false);

    private async Task<StatisticsSnapshot> LoadCachedAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token, bool includeOpening)
    {
        var scope = server.StatisticsCacheScope;
        var snapshot = await cache.GetAsync(start, finish, displayCurrency, includeOpening, async shared =>
        {
            var result = await LoadCoreAsync(start, finish, displayCurrency, shared, includeOpening).ConfigureAwait(false);
            if (scope != server.StatisticsCacheScope)
                throw new StatisticsCacheInvalidatedException();
            return result;
        }, token, scope).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (scope != server.StatisticsCacheScope) throw new StatisticsCacheInvalidatedException();
        return snapshot;
    }

    internal Task<StatisticsSnapshot> LoadCurrentAsync(DateTimeOffset asOfUtc, TimeZoneInfo displayTimeZone,
        string displayCurrency, CancellationToken token)
    {
        var localNow = TimeZoneInfo.ConvertTime(asOfUtc, displayTimeZone).DateTime;
        return LoadCoreAsync(localNow.Date, localNow, displayCurrency, token, includeOpening: true, asOfUtc: asOfUtc);
    }

    private async Task<StatisticsSnapshot> LoadCoreAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token, bool includeOpening, DateTimeOffset? asOfUtc = null)
    {
        var operations = await server.GetBudgetOperationsAsync(start, finish, token, includeOpening).ConfigureAwait(false);
        if (asOfUtc is { } boundary)
            operations = operations.Where(operation => operation.IsOpening || operation.AddedOnUtc is not { } instant
                || instant <= boundary).ToArray();
        var entries = operations.Select(operation =>
        {
            var date = operation.IsOpening ? (DateOnly?)null : server.DisplayOperationDate(null, operation.AddedOnUtc) is { } added
                ? DateOnly.FromDateTime(added) : operation.ReceiptDate;
            return new Entry(operation.IsIncome, operation.Currency, operation.Amount, date, operation.RateDate ?? date,
                operation.IsOpening, operation.IsOpening ? Math.Max(1, operation.OpeningCount) : 1);
        }).ToArray();
        var rates = await exchangeRates.GetAsync(entries.Where(entry => entry.RateDate.HasValue)
            .Select(entry => (entry.Currency, entry.RateDate!.Value)), displayCurrency, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => Calculate(entries, DateOnly.FromDateTime(start), DateOnly.FromDateTime(finish),
            displayCurrency, rates), token).ConfigureAwait(false);
    }

    private static StatisticsSnapshot Calculate(Entry[] entries, DateOnly start, DateOnly finish, string displayCurrency,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        var converted = entries.Select(entry => Convert(entry, displayCurrency, rates)).ToArray();
        var omitted = converted.Where(entry => entry.Currency is null).ToArray();
        var convertedMode = displayCurrency.Length > 0;
        var buckets = PeriodBuckets(start, finish).ToArray();
        var groups = converted.Where(entry => entry.Currency is not null)
            .GroupBy(entry => entry.Currency!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Currency: group.Key, Entries: group.ToArray())).ToList();
        if (convertedMode && groups.Count == 0 && entries.Length > 0)
            groups.Add((displayCurrency, []));
        var currencies = groups.Select(group =>
        {
            var missing = convertedMode ? omitted : omitted.Where(entry => entry.Original.Currency == group.Currency
                || !HistoricalExchangeRates.IsCurrencyCode(entry.Original.Currency)).ToArray();
            var summary = Summarize(group.Currency, group.Entries.Where(entry => !entry.Original.IsOpening).ToArray(),
                convertedMode ? omitted.Where(entry => !entry.Original.IsOpening).ToArray() : [], buckets);
            var periodRows = group.Entries.Concat(missing).Where(entry => !entry.Original.IsOpening).ToArray();
            var dailyPoints = Daily(periodRows, start, finish);
            return summary with
            {
                RemainingPoints = Remaining(group.Entries.Concat(missing).ToArray(), start, finish),
                RemainingWarning = RemainingWarning(group.Entries.Concat(missing).ToArray()),
                DailyPoints = dailyPoints,
                MonthlyPoints = Monthly(dailyPoints)
            };
        }).ToArray();
        return new StatisticsSnapshot(entries.Length != 0,
            omitted.Count(entry => !entry.Original.IsOpening && entry.Original.IsIncome),
            omitted.Count(entry => !entry.Original.IsOpening && !entry.Original.IsIncome), currencies);
    }

    private static IReadOnlyList<StatisticsDailyPoint> Daily(ConvertedEntry[] rows, DateOnly start, DateOnly finish)
    {
        var missingIncomeDate = rows.Any(row => row.Original.IsIncome && row.Original.ReportDate is null);
        var missingExpenseDate = rows.Any(row => !row.Original.IsIncome && row.Original.ReportDate is null);
        var byDate = rows.Where(row => row.Original.ReportDate.HasValue)
            .GroupBy(row => row.Original.ReportDate!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var points = new List<StatisticsDailyPoint>(Math.Max(0, finish.DayNumber - start.DayNumber + 1));
        for (var day = start.DayNumber; day <= finish.DayNumber; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            var dailyRows = byDate.GetValueOrDefault(date) ?? [];
            points.Add(new StatisticsDailyPoint(date,
                missingIncomeDate ? null : Total(dailyRows, isIncome: true, requireComplete: true),
                missingExpenseDate ? null : Total(dailyRows, isIncome: false, requireComplete: true)));
        }
        return points;
    }

    private static IReadOnlyList<StatisticsPoint> Monthly(IReadOnlyList<StatisticsDailyPoint> dailyPoints) =>
        dailyPoints.GroupBy(point => new DateOnly(point.Date.Year, point.Date.Month, 1))
            .Select(month => new StatisticsPoint($"{month.Key:MM.yyyy}",
                month.Any(point => point.Income is null) ? null : month.Sum(point => point.Income!.Value),
                month.Any(point => point.Expenses is null) ? null : month.Sum(point => point.Expenses!.Value)))
            .ToArray();

    private static IReadOnlyList<StatisticsRemainingPoint> Remaining(ConvertedEntry[] rows, DateOnly start, DateOnly finish)
    {
        var unknownDate = rows.Any(row => !row.Original.IsOpening && row.Original.ReportDate is null);
        var firstUnknownAmount = rows.Where(row => row.Currency is null)
            .Select(row => row.Original.ReportDate ?? start).DefaultIfEmpty(DateOnly.MaxValue).Min();
        var balance = rows.Where(row => row.Original.IsOpening && row.Currency is not null)
            .Sum(row => row.ConvertAmount(row.Original.Amount) * (row.Original.IsIncome ? 1m : -1m));
        var changes = rows.Where(row => !row.Original.IsOpening && row.Currency is not null && row.Original.ReportDate.HasValue)
            .GroupBy(row => row.Original.ReportDate!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(row =>
                row.ConvertAmount(row.Original.Amount) * (row.Original.IsIncome ? 1m : -1m)));
        var points = new List<StatisticsRemainingPoint>(Math.Max(0, finish.DayNumber - start.DayNumber + 1));
        for (var day = start.DayNumber; day <= finish.DayNumber; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            balance += changes.GetValueOrDefault(date);
            points.Add(new StatisticsRemainingPoint(date,
                unknownDate || date >= firstUnknownAmount ? null : balance));
        }
        return points;
    }

    private static string RemainingWarning(ConvertedEntry[] rows)
    {
        if (rows.Any(row => row.Original.IsOpening && row.Currency is null))
            return "Начальный остаток недоступен: в истории отсутствует валюта или курс.";
        if (rows.Any(row => !row.Original.IsOpening && row.Original.ReportDate is null))
            return "Остаток недоступен: у операции отсутствует дата.";
        if (rows.Any(row => row.Currency is null))
            return "После операции без валюты или курса остаток недоступен.";
        return "";
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

        return new StatisticsCurrency(currency, income, expenses, omitted.Length == 0 ? income - expenses : null,
            points);
    }

    private static decimal? Total(ReadOnlySpan<ConvertedEntry> rows, bool isIncome, bool requireComplete = false)
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
        return omitted && (requireComplete || !converted) ? null : sum;
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
        DateOnly? RateDate, bool IsOpening = false, long OpeningCount = 1);

    private sealed record ConvertedEntry(Entry Original, string? Currency, decimal? Rate)
    {
        public decimal ConvertAmount(decimal amount) =>
            (Rate is { } rate ? HistoricalExchangeRates.ConvertAmount(amount, rate) : amount) * Original.OpeningCount;
    }

    private sealed record PeriodBucket(DateOnly Start, DateOnly End, string Label);
}
