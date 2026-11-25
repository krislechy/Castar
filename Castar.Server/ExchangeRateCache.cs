using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Castar.Api;
using Castar.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Castar.Server;

internal sealed class ExchangeRateCache(
    IServiceScopeFactory scopes, IHttpClientFactory httpClients, ILogger<ExchangeRateCache> logger) : IDisposable
{
    internal const string HttpClientName = "Frankfurter";
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryAfterMiss = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim refreshGate = new(1);
    private readonly MemoryCache retry = new(new MemoryCacheOptions { SizeLimit = 4096 });

    public async Task<CurrencyRateDto[]> GetAsync(string target, CurrencyRateKeyDto[] keys,
        CancellationToken cancellationToken)
    {
        var conversions = keys.Where(key => key.Currency != target).ToArray();
        if (conversions.Length == 0)
            return keys.Select(key => new CurrencyRateDto(key.Currency, key.Date, key.Date, 1m)).ToArray();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LookupBudget);
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        database.Database.SetCommandTimeout(2);
        var rows = await ReadAsync(database, target, conversions, track: false, cancellationToken);
        if (GetMissing(target, conversions, rows).Length != 0)
        {
            try
            {
                await refreshGate.WaitAsync(budget.Token);
                try
                {
                    // A previous request may already have filled an overlapping set while we waited.
                    rows = await ReadAsync(database, target, conversions, track: true, budget.Token);
                    var missing = GetMissing(target, conversions, rows);
                    if (missing.Length != 0)
                        await FillAsync(database, target, rows, missing, budget.Token, cancellationToken);
                }
                finally
                {
                    refreshGate.Release();
                }
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The request still returns rates that were available before or fetched during the deadline.
            }
        }

        var result = new List<CurrencyRateDto>(keys.Length);
        foreach (var key in keys)
        {
            if (key.Currency == target)
                result.Add(new(key.Currency, key.Date, key.Date, 1m));
            else if (rows.TryGetValue((key.Currency, key.Date), out var row) && IsUsable(row))
                result.Add(new(row.Source, row.Date, row.QuotedOn, row.Rate));
        }
        return result.ToArray();
    }

    private static async Task<Dictionary<(string Currency, DateOnly Date), ExchangeRateEntry>> ReadAsync(
        CastarDbContext database, string target, CurrencyRateKeyDto[] keys, bool track, CancellationToken cancellationToken)
    {
        var requested = JsonSerializer.Serialize(keys.Distinct().Select(key => new { key.Currency, key.Date }));
        // Seek the composite key for each requested pair, even when many unrelated dates are cached.
        IQueryable<ExchangeRateEntry> query = database.ExchangeRates.FromSql($"""
            SELECT rate.*
            FROM json_each({requested}) AS requested
            CROSS JOIN "ExchangeRates" AS rate
            WHERE rate."Target" = {target}
              AND rate."Source" = json_extract(requested.value, '$.Currency')
              AND rate."Date" = json_extract(requested.value, '$.Date')
            """);
        if (!track)
            query = query.AsNoTracking();
        return await query.ToDictionaryAsync(row => (row.Source, row.Date), cancellationToken);
    }

    private CurrencyRateKeyDto[] GetMissing(string target, CurrencyRateKeyDto[] keys,
        Dictionary<(string Currency, DateOnly Date), ExchangeRateEntry> rows)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        return keys.Where(key =>
            (!rows.TryGetValue((key.Currency, key.Date), out var row) || !IsFresh(row, today, now))
            && !retry.TryGetValue((key.Currency, target, key.Date), out _)).ToArray();
    }

    private static bool IsUsable(ExchangeRateEntry row) =>
        row.Rate > 0 && row.Date.DayNumber - row.QuotedOn.DayNumber is >= 0 and <= 7;

    private static bool IsFresh(ExchangeRateEntry row, DateOnly today, DateTime now) =>
        IsUsable(row) && ((today.DayNumber - row.Date.DayNumber >= 8 && row.QuotedOn == row.Date)
            || now - row.RetrievedUtc < (today.DayNumber - row.Date.DayNumber < 8
                ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(12)));

    private async Task FillAsync(CastarDbContext database, string target,
        Dictionary<(string Currency, DateOnly Date), ExchangeRateEntry> rows,
        CurrencyRateKeyDto[] missing, CancellationToken budget, CancellationToken cancellationToken)
    {
        using var client = httpClients.CreateClient(HttpClientName);
        var quotes = new ConcurrentBag<CurrencyRateDto>();
        var misses = new ConcurrentBag<CurrencyRateKeyDto>();
        try
        {
            await Parallel.ForEachAsync(Windows(missing),
                new ParallelOptions { CancellationToken = budget, MaxDegreeOfParallelism = 6 }, async (window, token) =>
            {
                var available = await FetchAsync(client, target, window, token);
                foreach (var quote in available)
                    quotes.Add(quote);
                var quotedDates = available.Select(quote => quote.Date).ToHashSet();
                foreach (var date in window.Dates)
                    if (!quotedDates.Contains(date))
                        misses.Add(new(window.Source, date));
            });
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Preserve partial results from windows that finished within the shared upstream budget.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        foreach (var quote in quotes)
        {
            if (!rows.TryGetValue((quote.Currency, quote.Date), out var row))
            {
                row = new() { Source = quote.Currency, Target = target, Date = quote.Date };
                rows.Add((row.Source, row.Date), row);
                database.ExchangeRates.Add(row);
            }
            else if (IsUsable(row) && row.QuotedOn > quote.QuotedOn)
            {
                misses.Add(new(quote.Currency, quote.Date));
                continue;
            }

            row.QuotedOn = quote.QuotedOn;
            row.Rate = quote.Rate;
            row.RetrievedUtc = now;
        }

        if (database.ChangeTracker.HasChanges())
            await database.SaveChangesAsync(cancellationToken);

        foreach (var miss in misses)
            retry.Set((miss.Currency, target, miss.Date), true,
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = RetryAfterMiss });
    }

    private static IEnumerable<RateWindow> Windows(CurrencyRateKeyDto[] keys)
    {
        foreach (var group in keys.GroupBy(key => key.Currency))
        {
            var dates = group.Select(key => key.Date).Order().ToArray();
            for (var first = 0; first < dates.Length;)
            {
                var from = dates[first].AddDays(-Math.Min(7, dates[first].DayNumber));
                var end = first + 1;
                while (end < dates.Length && dates[end].DayNumber - from.DayNumber < 366)
                    end++;
                yield return new(group.Key, from, dates[first..end]);
                first = end;
            }
        }
    }

    private async Task<CurrencyRateDto[]> FetchAsync(HttpClient client, string target, RateWindow window,
        CancellationToken cancellationToken)
    {
        var to = window.Dates[^1];
        var request = FormattableString.Invariant(
            $"rates?from={window.From:yyyy-MM-dd}&to={to:yyyy-MM-dd}&base={window.Source}&quotes={target}");
        RateRow?[]? data;
        try
        {
            using var response = await client.GetAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];
            data = await response.Content.ReadFromJsonAsync<RateRow?[]>(cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not refresh historical exchange rates: {Type}", error.GetType().Name);
            return [];
        }

        var valid = (data ?? []).OfType<RateRow>().Where(row => row.Rate > 0
            && row.Base == window.Source && row.Quote == target && row.Date >= window.From && row.Date <= to)
            .OrderBy(row => row.Date).ToArray();
        var result = new List<CurrencyRateDto>(window.Dates.Length);
        var index = 0;
        RateRow? previous = null;
        foreach (var date in window.Dates)
        {
            while (index < valid.Length && valid[index].Date <= date)
                previous = valid[index++];
            if (previous is { } available && date.DayNumber - available.Date.DayNumber <= 7)
                result.Add(new(window.Source, date, available.Date, available.Rate));
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        refreshGate.Dispose();
        retry.Dispose();
    }

    private sealed record RateWindow(string Source, DateOnly From, DateOnly[] Dates);
    private sealed record RateRow(DateOnly Date, string Base, string Quote, decimal Rate);
}
