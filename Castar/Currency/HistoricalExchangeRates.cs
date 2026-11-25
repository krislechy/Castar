using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Currency;

public sealed class HistoricalExchangeRates : IDisposable
{
    internal static IReadOnlyList<string> SupportedCurrencies => ManualCurrencySettings.Default.Currencies;

    private readonly HttpClient http;
    private readonly CastarServerClient? server;
    private readonly SemaphoreSlim requests = new(1);
    private readonly Dictionary<(string Source, string Target, DateOnly Date), CachedRate> cache = [];

    public HistoricalExchangeRates() : this(CreateHandler())
    {
    }

    public HistoricalExchangeRates(CastarServerClient server) : this(CreateHandler(), server)
    {
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    internal HistoricalExchangeRates(HttpMessageHandler handler) : this(handler, null)
    {
    }

    internal HistoricalExchangeRates(HttpMessageHandler handler, CastarServerClient? server)
    {
        this.server = server;
        http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.frankfurter.dev/v2/"),
            Timeout = TimeSpan.FromSeconds(6),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    internal async Task<IReadOnlyList<CurrencyCatalogEntry>> GetCurrencyCatalogAsync(CancellationToken token)
    {
        var currencies = await http.GetFromJsonAsync<CatalogCurrency?[]>("currencies", token).ConfigureAwait(false);
        var codes = (currencies ?? []).OfType<CatalogCurrency>()
            .Where(currency => currency is { Type: "currency", StartDate: not null, NumericCode.Length: 3 }
                && currency.NumericCode.All(char.IsAsciiDigit) && IsCurrencyCode(currency.Code))
            .DistinctBy(currency => currency.Code, StringComparer.Ordinal)
            .OrderBy(currency => currency.Code, StringComparer.Ordinal)
            .Select(currency => new CurrencyCatalogEntry(currency.Code, currency.Name ?? currency.Code)).ToArray();
        if (codes.Length == 0)
            throw new JsonException("Справочник исторических валют пуст.");
        return codes;
    }

    internal async Task<IReadOnlyDictionary<(string Currency, DateOnly Date), decimal>> GetAsync(
        IEnumerable<(string? Currency, DateOnly Date)> amounts, string? target, CancellationToken token)
    {
        var result = new Dictionary<(string Currency, DateOnly Date), decimal>();
        if (!IsCurrencyCode(target))
            return result;

        var keys = amounts.Where(amount => IsCurrencyCode(amount.Currency) && amount.Currency != target)
            .Select(amount => (Currency: amount.Currency!, amount.Date)).Distinct().ToArray();
        if (keys.Length == 0)
            return result;

        if (server is not null && await TryGetServerRatesAsync(server, target!, keys, result, token).ConfigureAwait(false))
            return result;

        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var source in keys.GroupBy(key => key.Currency))
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

    private static async Task<bool> TryGetServerRatesAsync(CastarServerClient server, string target,
        (string Currency, DateOnly Date)[] keys, Dictionary<(string Currency, DateOnly Date), decimal> result,
        CancellationToken token)
    {
        foreach (var batch in keys.Chunk(512))
        {
            CurrencyRateDto[] quotes;
            try
            {
                quotes = await server.GetExchangeRatesAsync(target,
                    batch.Select(key => new CurrencyRateKeyDto(key.Currency, key.Date)).ToArray(), token).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }
            catch (Exception error) when (!token.IsCancellationRequested && CastarServerClient.IsConnectionError(error))
            {
                break;
            }

            var requested = batch.ToHashSet();
            foreach (var quote in quotes)
                if (quote is { Rate: > 0 } && requested.Contains((quote.Currency, quote.Date))
                    && quote.QuotedOn <= quote.Date && quote.Date.DayNumber - quote.QuotedOn.DayNumber <= 7)
                    result[(quote.Currency, quote.Date)] = quote.Rate;
        }
        return true;
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

    public void Dispose() => http.Dispose();

    private sealed record RateRow(DateOnly Date, string Base, string Quote, decimal Rate);
    private sealed record CachedRate(decimal? Rate, DateTimeOffset ExpiresAt);
    private sealed record CatalogCurrency(
        [property: JsonPropertyName("iso_code")] string Code,
        [property: JsonPropertyName("iso_numeric")] string? NumericCode,
        string? Name,
        string Type,
        [property: JsonPropertyName("start_date")] DateOnly? StartDate);
}

internal sealed record CurrencyCatalogEntry(string Code, string Name);
