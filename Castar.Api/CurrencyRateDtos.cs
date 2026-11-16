using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record CurrencyRateQueryDto(
    [property: JsonRequired] string Target,
    [property: JsonRequired] CurrencyRateKeyDto[] Rates);

public sealed record CurrencyRateKeyDto(string Currency, [property: JsonRequired] DateOnly Date);

public sealed record CurrencyRateDto(string Currency, DateOnly Date, DateOnly QuotedOn, decimal Rate);
