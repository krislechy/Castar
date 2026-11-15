using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record ServerSettingsDto(bool TelegramEnabled, bool TelegramConfigured, bool ReceiptsEnabled,
    string ReceiptProvider, string[] ReceiptProviders, long Revision)
{
    public bool? NalogConfigured { get; init; }
}

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
}
