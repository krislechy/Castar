using Castar.Api;
using Castar.Domain.Models.SettingsModel;

namespace Castar.Server;

internal static class ExchangeRateEndpoints
{
    public static void AddExchangeRates(this IServiceCollection services)
    {
        services.AddHttpClient(ExchangeRateCache.HttpClientName, http =>
        {
            http.BaseAddress = new Uri("https://api.frankfurter.dev/v2/");
            http.Timeout = TimeSpan.FromSeconds(6);
            http.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        services.AddSingleton<ExchangeRateCache>();
    }

    public static void MapExchangeRates(this RouteGroupBuilder api) =>
        api.MapPost("/exchange-rates", QueryAsync);

    private static async Task<IResult> QueryAsync(CurrencyRateQueryDto query, ExchangeRateCache cache,
        CancellationToken cancellationToken)
    {
        if (ManualCurrencySettings.NormalizeCode(query.Target) is not { } target)
            return Invalid("Укажите трёхбуквенный код целевой валюты.");
        if (query.Rates is null || query.Rates.Length > 512)
            return Invalid("Запрос должен содержать до 512 курсов.");

        var keys = new List<CurrencyRateKeyDto>(query.Rates.Length);
        foreach (var key in query.Rates)
        {
            if (key is null || ManualCurrencySettings.NormalizeCode(key.Currency) is not { } currency)
                return Invalid("Укажите трёхбуквенные коды исходных валют.");
            keys.Add(new(currency, key.Date));
        }

        var rates = await cache.GetAsync(target, keys.Distinct().ToArray(), cancellationToken);
        return Results.Ok(rates);
    }

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);
}
