using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

internal sealed class ServerSettingsViewModel(CastarServerClient server) : BaseSettingsModel
{
    private (string Address, string Key)? connection;
    private CancellationTokenSource? request;
    private ServerSettingsDto? saved;
    private bool active;
    private bool requiresReload;
    private bool saving;
    private bool recheckAfterSave;
    private bool telegramEnabled;
    private bool receiptsEnabled;
    private string? receiptProvider;
    private string status = "";

    public bool HasConnection => connection is not null;
    public bool CanReload => active && HasConnection && request is null;
    public bool CanEdit => CanReload && saved is not null;
    public bool CanEditTelegram => CanEdit && (saved?.TelegramConfigured == true || TelegramEnabled);
    public bool CanSave => CanEdit && !requiresReload && !recheckAfterSave && saved is { } current
        && ReceiptProviders.Any(provider => provider.Id == ReceiptProvider)
        && (!TelegramEnabled || current.TelegramConfigured)
        && (TelegramEnabled != current.TelegramEnabled || ReceiptsEnabled != current.ReceiptsEnabled
            || ReceiptProvider != current.ReceiptProvider);
    public string TelegramStatus => saved?.TelegramConfigured == false ? "На сервере не задан токен Telegram." : "";
    public ProviderOption[] ReceiptProviders { get; private set; } = [];

    public bool TelegramEnabled
    {
        get => telegramEnabled;
        set
        {
            if (SetField(ref telegramEnabled, value))
            {
                OnPropertyChanged(nameof(CanEditTelegram));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool ReceiptsEnabled
    {
        get => receiptsEnabled;
        set
        {
            if (SetField(ref receiptsEnabled, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string? ReceiptProvider
    {
        get => receiptProvider;
        set
        {
            if (SetField(ref receiptProvider, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public string Status { get => status; private set => SetField(ref status, value); }

    public Task OpenAsync(string address, string? protectedKey)
    {
        active = true;
        (string Address, string Key)? next = string.IsNullOrEmpty(protectedKey) ? null : (address, protectedKey);
        if (connection != next)
        {
            CancelRequest();
            connection = next;
            OnPropertyChanged(nameof(HasConnection));
            ApplySettings(null);
            Status = "";
        }

        NotifyActions();
        return saved is null || recheckAfterSave ? ReloadAsync() : Task.CompletedTask;
    }

    public void Close()
    {
        active = false;
        // The server may have committed before the client stopped waiting for its response.
        if (saving)
            recheckAfterSave = true;
        if (request is not null)
            Status = "";
        CancelRequest();
        NotifyActions();
    }

    public Task ReloadAsync() => CanReload
        ? RunAsync(server.GetServerSettingsAsync, "Загрузка…")
        : Task.CompletedTask;

    public Task SaveAsync()
    {
        if (!CanSave || saved is null || ReceiptProvider is null)
            return Task.CompletedTask;

        var update = new ServerSettingsWriteDto(TelegramEnabled, ReceiptsEnabled, ReceiptProvider, saved.Revision);
        return RunAsync(token => server.SaveServerSettingsAsync(update, token), "Сохранение…", isSave: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<ServerSettingsDto>> operation, string progress, bool isSave = false)
    {
        using var current = new CancellationTokenSource();
        request = current;
        saving = isSave;
        Status = progress;
        NotifyActions();
        try
        {
            var result = await operation(current.Token);
            if (request == current)
            {
                ApplySettings(result);
                Status = "";
            }
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == current)
            {
                if (error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Conflict })
                    requiresReload = true;
                Status = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                    ? "Обновите Castar Server для доступа к настройкам."
                    : CastarServerClient.DescribeError(error);
            }
        }
        finally
        {
            if (request == current)
            {
                request = null;
                saving = false;
                NotifyActions();
            }
        }
    }

    private void CancelRequest()
    {
        var previous = request;
        request = null;
        saving = false;
        previous?.Cancel();
    }

    private void ApplySettings(ServerSettingsDto? value)
    {
        saved = value;
        requiresReload = false;
        recheckAfterSave = false;
        TelegramEnabled = value?.TelegramEnabled ?? false;
        ReceiptsEnabled = value?.ReceiptsEnabled ?? false;
        ReceiptProviders = value?.ReceiptProviders.Select(id => new ProviderOption(id, id switch
        {
            "serbia" => "Налоговая Сербии",
            "nalog" => "ФНС России",
            _ => id
        })).ToArray() ?? [];
        OnPropertyChanged(nameof(ReceiptProviders));
        receiptProvider = value?.ReceiptProvider;
        OnPropertyChanged(nameof(ReceiptProvider));
        OnPropertyChanged(nameof(TelegramStatus));
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditTelegram));
        OnPropertyChanged(nameof(CanSave));
    }

    public sealed record ProviderOption(string Id, string Label);
}
