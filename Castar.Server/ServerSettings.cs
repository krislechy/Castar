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
    private readonly bool telegramConfigured;
    private readonly bool nalogConfigured;
    private readonly string[] providers;
    private readonly SemaphoreSlim writes = new(1);
    private ServerSettingsWriteDto current;

    public ServerSettings(IConfiguration configuration, SettingsModel settings, IEnumerable<IReceiptProvider> providers)
    {
        this.settings = settings;
        path = CastarData.GetSqlitePath(configuration) + ".settings.json";
        telegramConfigured = !string.IsNullOrWhiteSpace(settings.TelegramBot.TelegramBotApi_KEY);
        nalogConfigured = !string.IsNullOrWhiteSpace(settings.NalogFNS.INN) && !string.IsNullOrWhiteSpace(settings.NalogFNS.Password);
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
            if (saved is null || saved.Revision <= 0 || provider is null)
                throw new InvalidDataException($"Invalid server settings in {path}.");

            Apply(saved with { ReceiptProvider = provider });
            return;
        }

        if (current.TelegramEnabled && !telegramConfigured)
            throw new InvalidDataException("Set Castar__Telegram__Token when enabling the Telegram bot.");

        var configuredProvider = FindProvider(current.ReceiptProvider);
        if (current.ReceiptsEnabled && configuredProvider is null)
            throw new InvalidDataException("Castar__Receipts__Provider does not match any registered receipt provider.");

        Apply(current with { ReceiptProvider = configuredProvider ?? SerbiaReceiptProvider.ProviderId });
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
        if (requested.TelegramEnabled && !telegramConfigured)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "На сервере не задан токен Telegram.");
        var provider = FindProvider(requested.ReceiptProvider);
        if (provider is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Выберите доступного поставщика чеков.");
        if (requested.ReceiptsEnabled && provider == Nalog.ProviderId && !nalogConfigured)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "На сервере не заданы ИНН и пароль ФНС.");

        await writes.WaitAsync(cancellationToken);
        try
        {
            if (requested.Revision != current.Revision)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "Настройки изменились на сервере. Обновите их и повторите попытку.");
            }

            var next = requested with { ReceiptProvider = provider };
            if (next == current)
                return Results.Ok(ToDto(current));

            next = next with { Revision = checked(current.Revision + 1) };
            await SaveAsync(next, cancellationToken);
            Apply(next);
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

    private ServerSettingsDto ToDto(ServerSettingsWriteDto value) => new(
        value.TelegramEnabled, telegramConfigured, value.ReceiptsEnabled, value.ReceiptProvider, providers, value.Revision);

    private async Task SaveAsync(ServerSettingsWriteDto value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var file = new FileStream(temporaryPath, options))
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

    private void Apply(ServerSettingsWriteDto value)
    {
        settings.Receipts = new ReceiptSettings { IsEnabled = value.ReceiptsEnabled, Provider = value.ReceiptProvider };
        settings.Systems.IsEnableTelegramBot = value.TelegramEnabled;
        Volatile.Write(ref current, value);
    }

    public void Dispose() => writes.Dispose();
}
