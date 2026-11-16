using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;

namespace Castar.Server;

internal sealed class ServerSettings : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SettingsModel settings;
    private readonly string path;
    private readonly string? defaultTelegramToken;
    private readonly NalogCredentialsWriteDto? defaultNalogCredentials;
    private readonly Dictionary<string, IReceiptProvider> providers;
    private readonly SemaphoreSlim writes = new(1);
    private StoredSettings current;

    public ServerSettings(IConfiguration configuration, SettingsModel settings, IEnumerable<IReceiptProvider> providers)
    {
        this.settings = settings;
        path = CastarData.GetSqlitePath(configuration) + ".settings.json";
        defaultTelegramToken = settings.TelegramBot.TelegramBotApi_KEY;
        if (!string.IsNullOrWhiteSpace(settings.NalogFNS.INN) && !string.IsNullOrWhiteSpace(settings.NalogFNS.Password))
            defaultNalogCredentials = new(settings.NalogFNS.INN, settings.NalogFNS.Password);
        this.providers = providers.OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        current = new(settings.Systems.IsEnableTelegramBot, settings.Receipts.IsEnabled, settings.Receipts.Provider,
            Random.Shared.Next(1, int.MaxValue));
    }

    public async Task InitializeAsync()
    {
        await using var input = OpenSavedSettings();
        if (input is not null)
        {
            var saved = await JsonSerializer.DeserializeAsync<StoredSettings>(input, JsonOptions);
            var provider = FindProvider(saved?.ReceiptProvider);
            if (saved is null || saved.Revision <= 0 || provider is null ||
                ValidateCredentials(saved.TelegramToken, saved.NalogCredentials) is not null ||
                !HasValidSavedAuthentication(saved))
                throw new InvalidDataException($"Invalid server settings in {path}.");

            var policy = settings.ManualCurrencies;
            if (saved.ManualCurrency is { } savedCurrency)
            {
                if (!ManualCurrencySettings.TryCreate(savedCurrency.DefaultCurrency, savedCurrency.Currencies,
                    out var savedPolicy, out _))
                    throw new InvalidDataException($"Invalid manual currency settings in {path}.");
                policy = savedPolicy;
            }

            Apply(saved with { ReceiptProvider = provider.Id, ManualCurrency = ToDto(policy) }, policy);
            return;
        }

        if (current.TelegramEnabled && !HasTelegramToken(current))
            throw new InvalidDataException("Set Castar__Telegram__Token when enabling the Telegram bot.");

        var configuredProvider = FindProvider(current.ReceiptProvider);
        if (current.ReceiptsEnabled && configuredProvider is null)
            throw new InvalidDataException("Castar__Receipts__Provider does not match any registered receipt provider.");

        var initialPolicy = settings.ManualCurrencies;
        Apply(current with
        {
            ReceiptProvider = configuredProvider?.Id ?? SerbiaReceiptProvider.ProviderId,
            ManualCurrency = ToDto(initialPolicy)
        }, initialPolicy);
    }

    private FileStream? OpenSavedSettings()
    {
        try
        {
            return File.OpenRead(path);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public ServerSettingsDto Read() => ToDto(Volatile.Read(ref current));

    public async Task<IResult> UpdateAsync(ServerSettingsWriteDto requested, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            if (requested.Revision != current.Revision)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "Настройки изменились на сервере. Обновите их и повторите попытку.");
            }

            var provider = FindProvider(requested.ReceiptProvider);
            if (provider is null)
                return Invalid("Выберите доступного поставщика чеков.");
            if (ValidateCredentials(requested.TelegramToken, requested.NalogCredentials) is { } error)
                return Invalid(error);

            var policy = settings.ManualCurrencies;
            var manualCurrency = current.ManualCurrency;
            if (requested.ManualCurrency is { } requestedCurrency)
            {
                if (!ManualCurrencySettings.TryCreate(requestedCurrency.DefaultCurrency, requestedCurrency.Currencies,
                    out var requestedPolicy, out var currencyError))
                    return Invalid(currencyError!);

                if (requestedPolicy.DefaultCurrency != policy.DefaultCurrency ||
                    !requestedPolicy.Currencies.SequenceEqual(policy.Currencies, StringComparer.Ordinal))
                {
                    policy = requestedPolicy;
                    manualCurrency = ToDto(policy);
                }
            }

            var next = current with
            {
                TelegramEnabled = requested.TelegramEnabled,
                ReceiptsEnabled = requested.ReceiptsEnabled,
                ReceiptProvider = provider.Id,
                TelegramToken = requested.TelegramToken ?? current.TelegramToken,
                NalogCredentials = requested.NalogCredentials ?? current.NalogCredentials,
                ManualCurrency = manualCurrency
            };
            if (requested.ReceiptAuthentication is { } authentication)
            {
                if (ValidateAuthentication(provider.Authentication, authentication) is { } authenticationError)
                    return Invalid(authenticationError);
                if (provider.Id == Nalog.ProviderId && authentication.Values is { } nalogValues)
                {
                    if (requested.NalogCredentials is not null)
                        return Invalid("Передайте данные входа поставщика одним способом.");

                    var credentials = new NalogCredentialsWriteDto(nalogValues["inn"].Trim(), nalogValues["password"]);
                    if (ValidateCredentials(null, credentials) is { } nalogError)
                        return Invalid(nalogError);
                    next = next with { NalogCredentials = credentials };
                }
                else if (provider.Id != Nalog.ProviderId)
                {
                    next = WithAuthentication(next, provider, authentication);
                }
            }

            if (next.TelegramEnabled && !HasTelegramToken(next))
                return Invalid("На сервере не задан токен Telegram.");
            if (next.ReceiptsEnabled && !GetAuthentication(next, provider).UseAnonymous &&
                provider.Authentication.Fields.Any(field => field.Required) && !HasCredentials(next, provider))
                return Invalid("Установите данные для входа выбранного поставщика чеков.");
            if (next == current)
                return Results.Ok(ToDto(current));

            next = next with { Revision = checked(current.Revision + 1) };
            await SaveAsync(next, cancellationToken);
            Apply(next, policy);
            return Results.Ok(ToDto(next));
        }
        finally
        {
            writes.Release();
        }
    }

    private IReceiptProvider? FindProvider(string? id) =>
        id is null ? null : providers.GetValueOrDefault(id.Trim());

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);

    private static string? ValidateCredentials(string? telegramToken, NalogCredentialsWriteDto? credentials)
    {
        if (telegramToken is { } token)
        {
            var separator = token.IndexOf(':');
            if (separator is < 1 or > 20 || token.Length - separator is < 21 or > 129
                || token[..separator].Any(character => !char.IsAsciiDigit(character))
                || token[(separator + 1)..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                return "Некорректный формат токена Telegram.";
        }

        if (credentials is not null &&
            (credentials.Inn is not { Length: 10 or 12 } || credentials.Inn.Any(character => !char.IsAsciiDigit(character))
             || string.IsNullOrWhiteSpace(credentials.Password) || credentials.Password.Length > 1024
             || credentials.Password.Any(char.IsControl)))
            return "Укажите ИНН и пароль ФНС в допустимом формате.";

        return null;
    }

    private static string? ValidateAuthentication(ReceiptProviderAuthentication capabilities,
        ReceiptAuthenticationWriteDto authentication)
    {
        if (!SupportsMode(capabilities, authentication.UseAnonymous))
            return "Поставщик чеков не поддерживает выбранный режим входа.";
        if (authentication.Values is not { } values)
            return null;
        return authentication.UseAnonymous ? "При анонимном входе данные для входа не требуются." :
            ValidateCredentialFields(capabilities, values);
    }

    private static bool SupportsMode(ReceiptProviderAuthentication capabilities, bool useAnonymous) =>
        useAnonymous ? capabilities.SupportsAnonymous : capabilities.SupportsCredentials;

    private static string? ValidateCredentialFields(ReceiptProviderAuthentication capabilities,
        Dictionary<string, string> values)
    {
        if (values.Any(item => !capabilities.Fields.Any(field => field.Key == item.Key)
            || string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 1024 || item.Value.Any(char.IsControl)))
            return "Введите допустимые данные для входа поставщика чеков.";
        return capabilities.Fields.Any(field => field.Required && !values.ContainsKey(field.Key))
            ? "Заполните обязательные поля поставщика чеков."
            : null;
    }

    private bool HasValidSavedAuthentication(StoredSettings value) =>
        value.ReceiptAuthenticationByProvider is not { } saved ||
        saved.All(profile => FindProvider(profile.Key) is not { } provider ||
            profile.Value is { } authentication && SupportsMode(provider.Authentication, authentication.UseAnonymous)
            && (authentication.Values is null || provider.Authentication.SupportsCredentials &&
                ValidateCredentialFields(provider.Authentication, authentication.Values) is null));

    private static ReceiptAuthenticationWriteDto GetAuthentication(StoredSettings value, IReceiptProvider provider) =>
        value.ReceiptAuthenticationByProvider?.GetValueOrDefault(provider.Id) ?? new(provider.Authentication.SupportsAnonymous);

    private bool HasCredentials(StoredSettings value, IReceiptProvider provider) =>
        provider.Id == Nalog.ProviderId ? EffectiveNalogCredentials(value) is not null :
            GetAuthentication(value, provider).Values is { Count: > 0 };

    private static StoredSettings WithAuthentication(StoredSettings value, IReceiptProvider provider,
        ReceiptAuthenticationWriteDto requested)
    {
        var previous = GetAuthentication(value, provider);
        var credentials = requested.Values is { } submitted
            ? new Dictionary<string, string>(submitted, StringComparer.Ordinal)
            : previous.Values;
        if (requested.UseAnonymous == previous.UseAnonymous && SameValues(credentials, previous.Values))
            return value;

        var profiles = value.ReceiptAuthenticationByProvider is { } saved
            ? new Dictionary<string, ReceiptAuthenticationWriteDto>(saved, StringComparer.Ordinal)
            : new Dictionary<string, ReceiptAuthenticationWriteDto>(StringComparer.Ordinal);
        profiles[provider.Id] = new(requested.UseAnonymous, credentials);
        return value with { ReceiptAuthenticationByProvider = profiles };
    }

    private static bool SameValues(Dictionary<string, string>? first, Dictionary<string, string>? second) =>
        ReferenceEquals(first, second) || (first is not null && second is not null
            && first.Count == second.Count && first.All(item => second.TryGetValue(item.Key, out var value) && value == item.Value));

    private bool HasTelegramToken(StoredSettings value) =>
        !string.IsNullOrWhiteSpace(value.TelegramToken ?? defaultTelegramToken);

    private NalogCredentialsWriteDto? EffectiveNalogCredentials(StoredSettings value) =>
        value.NalogCredentials ?? defaultNalogCredentials;

    private ServerSettingsDto ToDto(StoredSettings value) => new(
        value.TelegramEnabled, HasTelegramToken(value), value.ReceiptsEnabled, value.ReceiptProvider,
        providers.Values.Select(provider => provider.Id).ToArray(), value.Revision)
    {
        NalogConfigured = EffectiveNalogCredentials(value) is not null,
        ManualCurrency = value.ManualCurrency is { } currency
            ? currency with { Currencies = currency.Currencies.ToArray() }
            : null,
        ReceiptAuthenticationProfiles = providers.Values.Select(provider => new ReceiptAuthenticationProfileDto(
            provider.Id, provider.Authentication.SupportsAnonymous, provider.Authentication.SupportsCredentials,
            GetAuthentication(value, provider).UseAnonymous, HasCredentials(value, provider),
            provider.Authentication.Fields.Select(field =>
                new ReceiptCredentialFieldDto(field.Key, field.Label, field.Required, field.Secret)).ToArray())).ToArray()
    };

    private static ManualCurrencySettingsDto ToDto(ManualCurrencySettings policy) =>
        new(policy.DefaultCurrency, policy.Currencies.ToArray());

    private async Task SaveAsync(StoredSettings value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = CreatePrivateFile(temporaryPath, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, value, JsonOptions, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    internal static FileStream CreatePrivateFile(string path, FileOptions options = FileOptions.None)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var account = identity.User ?? throw new InvalidOperationException("Cannot identify the Castar Server Windows account.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
                4096, options, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = options,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private void Apply(StoredSettings value, ManualCurrencySettings policy)
    {
        settings.ManualCurrencies = policy;
        var credentials = EffectiveNalogCredentials(value);
        settings.NalogFNS = new() { INN = credentials?.Inn, Password = credentials?.Password };
        foreach (var provider in providers.Values)
        {
            var authentication = GetAuthentication(value, provider);
            var values = provider.Id == Nalog.ProviderId && credentials is not null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["inn"] = credentials.Inn,
                    ["password"] = credentials.Password
                }
                : authentication.Values;
            provider.ConfigureAuthentication(new ReceiptAuthenticationSettings(authentication.UseAnonymous, values));
        }
        settings.Receipts = new ReceiptSettings { IsEnabled = value.ReceiptsEnabled, Provider = value.ReceiptProvider };
        if (!value.TelegramEnabled)
            settings.Systems.IsEnableTelegramBot = false;
        settings.TelegramBot.TelegramBotApi_KEY = value.TelegramToken ?? defaultTelegramToken;
        if (value.TelegramEnabled)
            settings.Systems.IsEnableTelegramBot = true;
        Volatile.Write(ref current, value);
    }

    public void Dispose() => writes.Dispose();

    private sealed record StoredSettings(
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
        public ManualCurrencySettingsDto? ManualCurrency { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, ReceiptAuthenticationWriteDto>? ReceiptAuthenticationByProvider { get; init; }
    }
}
