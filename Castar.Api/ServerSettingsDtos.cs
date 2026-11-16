using System.Globalization;
using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record ServerSettingsDto(bool TelegramEnabled, bool TelegramConfigured, bool ReceiptsEnabled,
    string ReceiptProvider, string[] ReceiptProviders, long Revision)
{
    public bool? NalogConfigured { get; init; }
    public ManualCurrencySettingsDto? ManualCurrency { get; init; }
    public ReceiptAuthenticationProfileDto[]? ReceiptAuthenticationProfiles { get; init; }
    public TelegramPreferencesDto? TelegramPreferences { get; init; }
}

public sealed record TelegramPreferencesDto(string IncomeCreateEmoji, string IncomeUpdateEmoji,
    string IncomeDeleteEmoji, string PurchaseCreateEmoji, string PurchaseUpdateEmoji,
    string PurchaseDeleteEmoji, string ItemEmoji, long? TestChatId)
{
    public static bool IsValidChatId(long? value) =>
        value is null or (>= -4503599627370495 and <= 4503599627370495 and not 0);

    public bool IsValid() => IsValidChatId(TestChatId) &&
        new[] { IncomeCreateEmoji, IncomeUpdateEmoji, IncomeDeleteEmoji, PurchaseCreateEmoji,
            PurchaseUpdateEmoji, PurchaseDeleteEmoji, ItemEmoji }.All(IsValidEmoji);

    private static bool IsValidEmoji(string? value) => value is not null && value.Length <= 32 &&
        !value.Any(char.IsControl) && StringInfo.ParseCombiningCharacters(value).Length <= 1;
}

public sealed record ReceiptCredentialFieldDto(string Key, string Label, bool Required, bool Secret);

public sealed record ReceiptAuthenticationProfileDto(string Provider, bool SupportsAnonymous, bool SupportsCredentials,
    bool UseAnonymous, bool CredentialsConfigured, ReceiptCredentialFieldDto[] Fields);

public sealed record ReceiptAuthenticationWriteDto([property: JsonRequired] bool UseAnonymous,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? Values = null);

public sealed record ManualCurrencySettingsDto(
    [property: JsonRequired] string DefaultCurrency,
    [property: JsonRequired] string[] Currencies);

public sealed record NalogCredentialsWriteDto(
    [property: JsonRequired] string Inn,
    [property: JsonRequired] string Password);

public sealed record ServerSettingsWriteDto(
    [property: JsonRequired] bool TelegramEnabled,
    [property: JsonRequired] bool ReceiptsEnabled,
    [property: JsonRequired] string ReceiptProvider,
    [property: JsonRequired] long Revision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TelegramToken { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NalogCredentialsWriteDto? NalogCredentials { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReceiptAuthenticationWriteDto? ReceiptAuthentication { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManualCurrencySettingsDto? ManualCurrency { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TelegramPreferencesDto? TelegramPreferences { get; init; }
}
