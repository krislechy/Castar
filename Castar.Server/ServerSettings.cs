using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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
    private readonly string[] providers;
    private readonly SemaphoreSlim writes = new(1);
    private ServerSettingsWriteDto current;

    public ServerSettings(IConfiguration configuration, SettingsModel settings, IEnumerable<IReceiptProvider> providers)
    {
        this.settings = settings;
        path = CastarData.GetSqlitePath(configuration) + ".settings.json";
        defaultTelegramToken = settings.TelegramBot.TelegramBotApi_KEY;
        if (!string.IsNullOrWhiteSpace(settings.NalogFNS.INN) && !string.IsNullOrWhiteSpace(settings.NalogFNS.Password))
            defaultNalogCredentials = new(settings.NalogFNS.INN, settings.NalogFNS.Password);
        this.providers = providers.Select(provider => provider.Id).Order(StringComparer.Ordinal).ToArray();
        current = new(settings.Systems.IsEnableTelegramBot, settings.Receipts.IsEnabled, settings.Receipts.Provider,
            Random.Shared.Next(1, int.MaxValue));
    }

    public async Task InitializeAsync()
    {
        await using var input = OpenSavedSettings();
        if (input is not null)
        {
            var saved = await JsonSerializer.DeserializeAsync<ServerSettingsWriteDto>(input, JsonOptions);
            var provider = FindProvider(saved?.ReceiptProvider);
            if (saved is null || saved.Revision <= 0 || provider is null || ValidateCredentials(saved) is not null)
                throw new InvalidDataException($"Invalid server settings in {path}.");

            var policy = settings.ManualCurrencies;
            if (saved.ManualCurrency is { } savedCurrency)
            {
                if (!ManualCurrencySettings.TryCreate(savedCurrency.DefaultCurrency, savedCurrency.Currencies,
                    out var savedPolicy, out _))
                    throw new InvalidDataException($"Invalid manual currency settings in {path}.");
                policy = savedPolicy;
            }

            Apply(saved with { ReceiptProvider = provider, ManualCurrency = ToDto(policy) }, policy);
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
            ReceiptProvider = configuredProvider ?? SerbiaReceiptProvider.ProviderId,
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
            if (ValidateCredentials(requested) is { } error)
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

            var next = requested with
            {
                ReceiptProvider = provider,
                TelegramToken = requested.TelegramToken ?? current.TelegramToken,
                NalogCredentials = requested.NalogCredentials ?? current.NalogCredentials,
                ManualCurrency = manualCurrency
            };
            if (next.TelegramEnabled && !HasTelegramToken(next))
                return Invalid("На сервере не задан токен Telegram.");
            if (next.ReceiptsEnabled && provider == Nalog.ProviderId && EffectiveNalogCredentials(next) is null)
                return Invalid("На сервере не заданы ИНН и пароль ФНС.");
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

    private string? FindProvider(string? id)
    {
        var identifier = id?.Trim();
        return providers.FirstOrDefault(provider => string.Equals(provider, identifier, StringComparison.OrdinalIgnoreCase));
    }

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);

    private static string? ValidateCredentials(ServerSettingsWriteDto value)
    {
        if (value.TelegramToken is { } token)
        {
            var separator = token.IndexOf(':');
            if (separator is < 1 or > 20 || token.Length - separator is < 21 or > 129
                || token[..separator].Any(character => !char.IsAsciiDigit(character))
                || token[(separator + 1)..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                return "Некорректный формат токена Telegram.";
        }

        if (value.NalogCredentials is { } credentials &&
            (credentials.Inn is not { Length: 10 or 12 } || credentials.Inn.Any(character => !char.IsAsciiDigit(character))
             || string.IsNullOrWhiteSpace(credentials.Password) || credentials.Password.Length > 1024
             || credentials.Password.Any(char.IsControl)))
            return "Укажите ИНН и пароль ФНС в допустимом формате.";

        return null;
    }

    private bool HasTelegramToken(ServerSettingsWriteDto value) =>
        !string.IsNullOrWhiteSpace(value.TelegramToken ?? defaultTelegramToken);

    private NalogCredentialsWriteDto? EffectiveNalogCredentials(ServerSettingsWriteDto value) =>
        value.NalogCredentials ?? defaultNalogCredentials;

    private ServerSettingsDto ToDto(ServerSettingsWriteDto value) => new(
        value.TelegramEnabled, HasTelegramToken(value), value.ReceiptsEnabled, value.ReceiptProvider, providers, value.Revision)
    {
        NalogConfigured = EffectiveNalogCredentials(value) is not null,
        ManualCurrency = value.ManualCurrency is { } currency
            ? currency with { Currencies = currency.Currencies.ToArray() }
            : null
    };

    private static ManualCurrencySettingsDto ToDto(ManualCurrencySettings policy) =>
        new(policy.DefaultCurrency, policy.Currencies.ToArray());

    private async Task SaveAsync(ServerSettingsWriteDto value, CancellationToken cancellationToken)
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

    private void Apply(ServerSettingsWriteDto value, ManualCurrencySettings policy)
    {
        settings.ManualCurrencies = policy;
        var credentials = EffectiveNalogCredentials(value);
        settings.NalogFNS = new() { INN = credentials?.Inn, Password = credentials?.Password };
        settings.Receipts = new ReceiptSettings { IsEnabled = value.ReceiptsEnabled, Provider = value.ReceiptProvider };
        if (!value.TelegramEnabled)
            settings.Systems.IsEnableTelegramBot = false;
        settings.TelegramBot.TelegramBotApi_KEY = value.TelegramToken ?? defaultTelegramToken;
        if (value.TelegramEnabled)
            settings.Systems.IsEnableTelegramBot = true;
        Volatile.Write(ref current, value);
    }

    public void Dispose() => writes.Dispose();
}
