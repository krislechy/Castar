using System.Text.Json.Serialization;

namespace Castar.Api;

public sealed record ServerSettingsDto(bool TelegramEnabled, bool TelegramConfigured, bool ReceiptsEnabled,
    string ReceiptProvider, string[] ReceiptProviders, long Revision);

public sealed record ServerSettingsWriteDto(
    [property: JsonRequired] bool TelegramEnabled,
    [property: JsonRequired] bool ReceiptsEnabled,
    [property: JsonRequired] string ReceiptProvider,
    [property: JsonRequired] long Revision);
