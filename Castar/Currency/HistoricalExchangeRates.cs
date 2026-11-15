using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Currency;

public sealed class HistoricalExchangeRates : IDisposable
{
    internal static IReadOnlyList<string> SupportedCurrencies { get; } =
        Array.AsReadOnly(new[] { "RSD", "RUB", "EUR", "USD", "GBP", "CHF" });

    private readonly HttpClient http;
    private readonly SemaphoreSlim requests = new(1);
    private readonly Dictionary<(string Source, string Target, DateOnly Date), CachedRate> cache = [];

    public HistoricalExchangeRates() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
    }

    internal HistoricalExchangeRates(HttpMessageHandler handler) => http = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://api.frankfurter.dev/v2/"),
        Timeout = TimeSpan.FromSeconds(6),
        MaxResponseContentBufferSize = 4 * 1024 * 1024
    };

    internal async Task<IReadOnlyDictionary<(string Currency, DateOnly Date), decimal>> GetAsync(
        IEnumerable<(string? Currency, DateOnly Date)> amounts, string? target, CancellationToken token)
    {
        var result = new Dictionary<(string Currency, DateOnly Date), decimal>();
        if (!IsCurrencyCode(target))
            return result;

        var sources = amounts.Where(amount => IsCurrencyCode(amount.Currency) && amount.Currency != target)
            .Select(amount => (Currency: amount.Currency!, amount.Date)).Distinct().GroupBy(amount => amount.Currency);
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var source in sources)
            {
                var dates = source.Select(amount => amount.Date).Order().ToArray();
                var now = DateTimeOffset.UtcNow;
                var missing = dates.Where(date => !cache.TryGetValue((source.Key, target!, date), out var value)
                    || value.ExpiresAt <= now).ToArray();
                if (missing.Length > 0)
                    await FetchAsync(source.Key, target!, missing, token).ConfigureAwait(false);

                foreach (var date in dates)
                {
                    if (cache.TryGetValue((source.Key, target!, date), out var cached) && cached.Rate is { } rate)
                        result[(source.Key, date)] = rate;
                }
            }
        }
        finally
        {
            requests.Release();
        }
        return result;
    }

    private async Task FetchAsync(string source, string target, DateOnly[] dates, CancellationToken token)
    {
        var from = dates[0].AddDays(-Math.Min(7, dates[0].DayNumber));
        var to = dates[^1];
        RateRow[] rates = [];
        try
        {
            var path = FormattableString.Invariant($"rates?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&base={source}&quotes={target}");
            using var response = await http.GetAsync(path, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                rates = await response.Content.ReadFromJsonAsync<RateRow[]>(cancellationToken: token).ConfigureAwait(false) ?? [];
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            // Retain original amounts and retry missing rates after the short cache interval.
        }

        var valid = rates.Where(row => row is { Rate: > 0 } && row.Base == source && row.Quote == target
            && row.Date >= from && row.Date <= to).OrderBy(row => row.Date).ToArray();
        var index = 0;
        RateRow? previous = null;
        var now = DateTimeOffset.UtcNow;
        foreach (var date in dates)
        {
            while (index < valid.Length && valid[index].Date <= date)
                previous = valid[index++];
            decimal? rate = previous is { } available && date.DayNumber - available.Date.DayNumber <= 7
                ? available.Rate : null;
            var lifetime = rate is null || date >= DateOnly.FromDateTime(now.UtcDateTime)
                ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(12);
            cache[(source, target, date)] = new CachedRate(rate, now + lifetime);
        }
    }

    internal static bool IsCurrencyCode(string? code) => code is { Length: 3 }
        && code.All(character => character is >= 'A' and <= 'Z');

    internal static decimal ConvertAmount(decimal amount, decimal rate) =>
        decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    internal static string Format(decimal amount, string? currency)
    {
        var number = amount.ToString("#,0.00##########################", CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(currency) ? number : $"{number} {currency}";
    }

    internal static string FormatTotals(IEnumerable<(decimal Amount, string? Currency)> amounts)
    {
        var totals = amounts.GroupBy(amount => amount.Currency)
            .Select(group => Format(group.Sum(amount => amount.Amount), group.Key));
        return string.Join(" · ", totals);
    }

    public void Dispose() => http.Dispose();

    private sealed record RateRow(DateOnly Date, string Base, string Quote, decimal Rate);
    private sealed record CachedRate(decimal? Rate, DateTimeOffset ExpiresAt);
}
