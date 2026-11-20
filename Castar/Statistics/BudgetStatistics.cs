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

internal sealed record StatisticsCurrency(string Currency, decimal? Income, decimal? Expenses, decimal? Balance)
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
                convertedMode ? omitted.Where(entry => !entry.Original.IsOpening).ToArray() : []);
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

    internal static decimal?[] AverageAccumulatedExpenses(DateOnly month, int historyMonths,
        IReadOnlyList<StatisticsDailyPoint> history)
    {
        var dayCount = DateTime.DaysInMonth(month.Year, month.Month);
        var dailyExpenses = history.ToDictionary(point => point.Date, point => point.Expenses);
        var averages = new decimal?[dayCount];
        Array.Fill(averages, 0m);
        for (var offset = historyMonths; offset > 0; offset--)
        {
            var previous = month.AddMonths(-offset);
            var previousDayCount = DateTime.DaysInMonth(previous.Year, previous.Month);
            decimal? accumulated = 0m;
            for (var day = 0; day < dayCount; day++)
            {
                if (day < previousDayCount)
                    accumulated += dailyExpenses.GetValueOrDefault(previous.AddDays(day), 0m);
                averages[day] += accumulated;
            }
        }
        for (var day = 0; day < dayCount; day++)
            averages[day] /= historyMonths;
        return averages;
    }

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

    private static StatisticsCurrency Summarize(string currency, ConvertedEntry[] converted, ConvertedEntry[] omitted)
    {
        var all = converted.Concat(omitted).ToArray();
        var income = Total(all, isIncome: true);
        var expenses = Total(all, isIncome: false);
        return new StatisticsCurrency(currency, income, expenses, omitted.Length == 0 ? income - expenses : null);
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

    private sealed record Entry(bool IsIncome, string? Currency, decimal Amount, DateOnly? ReportDate,
        DateOnly? RateDate, bool IsOpening = false, long OpeningCount = 1);

    private sealed record ConvertedEntry(Entry Original, string? Currency, decimal? Rate)
    {
        public decimal ConvertAmount(decimal amount) =>
            (Rate is { } rate ? HistoricalExchangeRates.ConvertAmount(amount, rate) : amount) * Original.OpeningCount;
    }
}
