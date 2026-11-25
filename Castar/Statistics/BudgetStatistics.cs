using Castar.Api;
using Castar.Currency;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Statistics;

internal sealed record StatisticsSnapshot(bool HasRecords, int UnconvertedIncomes, int UnconvertedExpenses,
    IReadOnlyList<StatisticsCurrency> Currencies)
{
    public DateOnly? EarliestOperationDate { get; init; }
    public DateTimeOffset? NextOperationUtc { get; init; }
    public DateTimeOffset? RatesValidUntil { get; init; }
}

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
internal sealed record StatisticsCategorySnapshot(IReadOnlyList<StatisticsCategoryAmount> Rows, bool HasRecords,
    long UnconvertedIncomes, long UnconvertedExpenses, IReadOnlyList<BudgetCategoryDto> Categories)
{
    public DateTimeOffset? RatesValidUntil { get; init; }
}
internal sealed record StatisticsCategoryAmount(bool IsIncome, Guid? CategoryId, string CategoryName, string? IconKey,
    string Currency, decimal? Amount, long UnconvertedCount);

internal sealed class BudgetStatistics
{
    internal const int MaxReserveHistoryMonths = 12;
    private readonly StatisticsSnapshotCache cache = new();
    private readonly BudgetDayCache dayCache;
    private readonly CastarServerClient server;
    private readonly HistoricalExchangeRates exchangeRates;
    private long financialCacheGeneration;

    public BudgetStatistics(CastarServerClient server, HistoricalExchangeRates exchangeRates)
        : this(server, exchangeRates, DefaultDayCachePath())
    {
    }

    internal BudgetStatistics(CastarServerClient server, HistoricalExchangeRates exchangeRates, string? dayCachePath)
    {
        this.server = server;
        this.exchangeRates = exchangeRates;
        dayCache = new BudgetDayCache(dayCachePath);
    }

    private static string? DefaultDayCachePath()
    {
#if DEBUG
        return null;
#else
        return Path.Combine(ClientSettings.DataDirectory, "ChartCache", "budget-days.v1");
#endif
    }

    internal void InvalidateCache() => cache.Invalidate();

    internal Task InvalidateFinancialCacheAsync(DateOnly? affectedDay)
    {
        Interlocked.Increment(ref financialCacheGeneration);
        var invalidation = dayCache.InvalidateAsync(server.StatisticsCacheIdentity, affectedDay);
        cache.Invalidate();
        return invalidation;
    }

    internal Task ClearChartCacheAsync()
    {
        Interlocked.Increment(ref financialCacheGeneration);
        var clear = dayCache.ClearAsync();
        cache.Invalidate();
        return clear;
    }

    internal Task<StatisticsSnapshot> LoadAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token) => LoadCachedAsync(start, finish, displayCurrency, token, includeOpening: true);

    internal Task<StatisticsSnapshot> LoadPeriodAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token) => LoadCachedAsync(start, finish, displayCurrency, token, includeOpening: false);

    internal async Task<StatisticsCategorySnapshot> LoadCategoriesAsync(DateTime start, DateTime finish,
        string displayCurrency, CancellationToken token)
    {
        var categoryTask = server.GetBudgetCategoriesAsync(token);
        var operationTask = server.GetBudgetCategoryAmountsAsync(start, finish, token);
        await Task.WhenAll(categoryTask, operationTask).ConfigureAwait(false);
        var operations = await operationTask.ConfigureAwait(false);
        var categories = await categoryTask.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (operations.Length == 0)
            return new([], false, 0, 0, categories);

        var keys = RateKeys(operations.Select(row => (row.Currency, row.RateDate)), displayCurrency);
        var rates = await exchangeRates.GetAsync(keys, displayCurrency, token).ConfigureAwait(false);
        return CalculateCategories(operations, categories, displayCurrency, rates, token) with
        {
            RatesValidUntil = RatesValidUntil(keys.Length)
        };
    }

    internal static StatisticsCategorySnapshot CalculateCategories(BudgetCategoryAmountDto[] operations,
        IReadOnlyList<BudgetCategoryDto> categories, string displayCurrency,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates, CancellationToken token)
    {
        var descriptions = categories.ToDictionary(category => (category.Kind, category.Id));
        var totals = new Dictionary<(bool IsIncome, Guid? CategoryId, string Currency), (decimal? Amount, long Unconverted)>();
        long missingIncomes = 0;
        long missingExpenses = 0;
        foreach (var row in operations)
        {
            token.ThrowIfCancellationRequested();
            var entry = Convert(new Entry(row.IsIncome, row.Currency, row.Amount, null, row.RateDate,
                Count: row.OperationCount), displayCurrency, rates);
            if (entry.Currency is null)
            {
                if (row.IsIncome)
                    missingIncomes += row.OperationCount;
                else
                    missingExpenses += row.OperationCount;
            }

            var key = (row.IsIncome, row.CategoryId, entry.Currency ?? displayCurrency);
            var total = totals.GetValueOrDefault(key);
            if (entry.Currency is null)
                total.Unconverted += row.OperationCount;
            else
                total.Amount = total.Amount.GetValueOrDefault() + entry.ConvertAmount(row.Amount);
            totals[key] = total;
        }

        var rows = totals.Select(row =>
        {
            var key = row.Key;
            var category = key.CategoryId is { } id
                ? descriptions.GetValueOrDefault((key.IsIncome ? BudgetEvents.Income : BudgetEvents.Purchase, id)) : null;
            var label = category?.Name ?? (key.CategoryId.HasValue ? "Категория недоступна" : "Без категории");
            return new StatisticsCategoryAmount(key.IsIncome, key.CategoryId, label, category?.IconKey, key.Currency,
                row.Value.Amount, row.Value.Unconverted);
        });
        return new(rows.OrderBy(row => row.IsIncome).ThenBy(row => row.Currency, StringComparer.Ordinal)
                .ThenByDescending(row => row.Amount).ThenBy(row => row.CategoryName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(), operations.Length != 0, missingIncomes, missingExpenses, categories);
    }

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
        var historyStart = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(-MaxReserveHistoryMonths);
        return LoadCoreAsync(historyStart, localNow, displayCurrency, token, includeOpening: true, asOfUtc: asOfUtc);
    }

    private async Task<StatisticsSnapshot> LoadCoreAsync(DateTime start, DateTime finish, string displayCurrency,
        CancellationToken token, bool includeOpening, DateTimeOffset? asOfUtc = null)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        token.ThrowIfCancellationRequested();
        var first = DateOnly.FromDateTime(start);
        var last = DateOnly.FromDateTime(finish);
        var scope = server.StatisticsCacheScope;
        var identity = server.StatisticsCacheIdentity;
        var generation = Interlocked.Read(ref financialCacheGeneration);
        var fullDays = start.TimeOfDay == TimeSpan.Zero
            && (asOfUtc.HasValue || finish.TimeOfDay.Ticks == TimeSpan.TicksPerDay - 1);
        var operations = fullDays
            ? await dayCache.GetAsync(identity, first, last, includeOpening, LoadDaysAsync, ReportDay,
                IsCurrent, token).ConfigureAwait(false)
            : await server.GetBudgetOperationsAsync(start, finish, token, includeOpening).ConfigureAwait(false);
        DateTimeOffset? nextOperation = null;
        if (asOfUtc is { } boundary)
        {
            nextOperation = operations.Where(operation => !operation.IsOpening && operation.AddedOnUtc > boundary
                    && ReportDay(operation) == last)
                .Min(operation => operation.AddedOnUtc);
            operations = operations.Where(operation => operation.IsOpening || operation.AddedOnUtc is not { } instant
                || instant <= boundary).ToArray();
        }
        var entries = operations.Select(operation =>
        {
            var date = operation.IsOpening ? (DateOnly?)null : ReportDay(operation);
            return new Entry(operation.IsIncome, operation.Currency, operation.Amount, date, operation.RateDate ?? date,
                operation.IsOpening, operation.IsOpening ? Math.Max(1, operation.OpeningCount) : 1);
        }).ToArray();
        var keys = RateKeys(entries.Select(entry => (entry.Currency, entry.RateDate)), displayCurrency);
        var rates = await exchangeRates.GetAsync(keys, displayCurrency, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent())
            throw new StatisticsCacheInvalidatedException();
        var result = Calculate(entries, first, last, displayCurrency, rates) with
        {
            NextOperationUtc = nextOperation,
            RatesValidUntil = RatesValidUntil(keys.Length)
        };
        if (!IsCurrent())
            throw new StatisticsCacheInvalidatedException();
        return result;

        bool IsCurrent() => generation == Interlocked.Read(ref financialCacheGeneration)
            && scope == server.StatisticsCacheScope;
    }

    private static (string? Currency, DateOnly Date)[] RateKeys(IEnumerable<(string? Currency, DateOnly? Date)> entries,
        string target) => HistoricalExchangeRates.IsCurrencyCode(target)
        ? entries.Where(entry => entry.Date.HasValue && entry.Currency != target && HistoricalExchangeRates.IsCurrencyCode(entry.Currency))
            .Select(entry => (entry.Currency, entry.Date!.Value)).Distinct().ToArray()
        : [];

    private static DateTimeOffset? RatesValidUntil(int rateKeys) =>
        rateKeys == 0 ? null : DateTimeOffset.UtcNow + StatisticsSnapshotCache.ExchangeRateLifetime;

    private DateOnly? ReportDay(BudgetOperationDto operation) =>
        server.DisplayOperationDate(null, operation.AddedOnUtc) is { } date ? DateOnly.FromDateTime(date) : operation.ReceiptDate;

    private Task<BudgetOperationDto[]> LoadDaysAsync(DateOnly first, DateOnly last, bool includeOpening, CancellationToken token) =>
        server.GetBudgetOperationsAsync(first.ToDateTime(TimeOnly.MinValue), last.ToDateTime(TimeOnly.MaxValue), token, includeOpening);

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
            omitted.Count(entry => !entry.Original.IsOpening && !entry.Original.IsIncome), currencies)
        {
            EarliestOperationDate = entries.Any(entry => entry.IsOpening) ? start : entries.Min(entry => entry.ReportDate)
        };
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
        DateOnly? RateDate, bool IsOpening = false, long Count = 1);

    private sealed record ConvertedEntry(Entry Original, string? Currency, decimal? Rate)
    {
        public decimal ConvertAmount(decimal amount) =>
            (Rate is { } rate ? HistoricalExchangeRates.ConvertAmount(amount, rate) : amount) * Original.Count;
    }
}
