using System.Diagnostics.CodeAnalysis;

namespace Castar.Domain.Models.SettingsModel;

public sealed class ManualCurrencySettings
{
    public static ManualCurrencySettings Default { get; } =
        new("RSD", ["RSD", "RUB", "EUR", "USD", "GBP", "CHF"]);

    private ManualCurrencySettings(string defaultCurrency, string[] currencies)
    {
        DefaultCurrency = defaultCurrency;
        Currencies = Array.AsReadOnly(currencies);
    }

    public string DefaultCurrency { get; }
    public IReadOnlyList<string> Currencies { get; }

    public bool Allows(string? code) =>
        NormalizeCode(code) is { } normalized && Currencies.Contains(normalized, StringComparer.Ordinal);

    public static string? NormalizeCode(string? code) =>
        code?.Trim() is { Length: 3 } value && value.All(char.IsAsciiLetter) ? value.ToUpperInvariant() : null;

    public static bool TryCreate(string? defaultCurrency, IEnumerable<string?>? currencies,
        [NotNullWhen(true)] out ManualCurrencySettings? policy, out string? validationError)
    {
        policy = null;
        if (currencies is null)
        {
            validationError = "Выберите доступные валюты.";
            return false;
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in currencies)
        {
            if (NormalizeCode(code) is not { } value || !seen.Add(value))
            {
                validationError = "Укажите уникальные трёхбуквенные коды валют.";
                return false;
            }
            normalized.Add(value);
        }

        if (normalized.Count == 0)
        {
            validationError = "Выберите хотя бы одну валюту.";
            return false;
        }

        if (NormalizeCode(defaultCurrency) is not { } defaultCode || !seen.Contains(defaultCode))
        {
            validationError = "Выберите валюту ручного добавления из доступных валют.";
            return false;
        }

        policy = new ManualCurrencySettings(defaultCode, normalized.ToArray());
        validationError = null;
        return true;
    }
}
