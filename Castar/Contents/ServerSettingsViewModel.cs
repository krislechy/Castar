using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

internal sealed class ServerSettingsViewModel(CastarServerClient server, BudgetEventConnection? events = null) : BaseSettingsModel
{
    private (string Address, string Key)? connection;
    private CancellationTokenSource? request;
    private ServerSettingsDto? saved;
    private bool active;
    private bool requiresReload;
    private bool saving;
    private bool recheckAfterSave;
    private bool pendingRefresh;
    private long? pendingRevision;
    private bool telegramEnabled;
    private bool receiptsEnabled;
    private string? receiptProvider;
    private ManualCurrencySettingsDto? manualCurrency;
    private TelegramPreferencesDto? telegramPreferences;
    private string telegramToken = "";
    private ReceiptAuthenticationProfileDto[] authenticationProfiles = [];
    private Dictionary<string, string>? receiptCredentials;
    private bool useAnonymous;
    private string status = "";
    private string backupActionLabel = "Создать резервную копию";
    private string backupStatus = "";

    public string BackupActionLabel { get => backupActionLabel; set => SetField(ref backupActionLabel, value); }
    public string BackupStatus { get => backupStatus; set => SetField(ref backupStatus, value); }

    public bool HasConnection => connection is not null;
    public ServerUpdateViewModel Updates { get; } = new(server, events);
    public bool CanReload => active && HasConnection && request is null;
    public bool CanEdit => CanReload && saved is not null;
    public bool SupportsCredentials => saved?.ReceiptAuthenticationProfiles is not null || saved?.NalogConfigured is not null;
    public bool SupportsManualCurrency => SavedManualCurrency is not null;
    public ManualCurrencySettingsDto? SavedManualCurrency => saved?.ManualCurrency;
    public bool SupportsTelegramPreferences => saved?.TelegramPreferences is not null;
    public TelegramPreferencesDto? TelegramPreferences
    {
        get => telegramPreferences;
        private set
        {
            if (SetField(ref telegramPreferences, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }
    public ManualCurrencySettingsDto? ManualCurrency
    {
        get => manualCurrency;
        private set
        {
            if (SetField(ref manualCurrency, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }
    public bool CanEditTelegram => CanEdit && (SupportsCredentials || saved?.TelegramConfigured == true || TelegramEnabled);
    public bool CanSave => CanEdit && !requiresReload && !recheckAfterSave && saved is { } current
        && ReceiptProviders.Any(provider => provider.Id == ReceiptProvider)
        && (!TelegramEnabled || current.TelegramConfigured || HasTelegramToken)
        && (telegramToken.Length == 0 || HasTelegramToken)
        && (!ReceiptsEnabled || AuthenticationProfile is not { } profile || UseAnonymous
            || profile.CredentialsConfigured || receiptCredentials is not null || !profile.Fields.Any(input => input.Required))
        && (TelegramEnabled != current.TelegramEnabled || ReceiptsEnabled != current.ReceiptsEnabled
            || ReceiptProvider != current.ReceiptProvider || HasTelegramToken || AuthenticationChanged
            || !SameCurrencySettings(ManualCurrency, current.ManualCurrency)
            || TelegramPreferences != current.TelegramPreferences);
    public string TelegramStatus => saved?.TelegramConfigured switch
    {
        true when SupportsCredentials => "Токен Telegram задан.",
        false => "На сервере не задан токен Telegram.",
        _ => ""
    };
    public ReceiptAuthenticationProfileDto? AuthenticationProfile =>
        authenticationProfiles.FirstOrDefault(profile => profile.Provider == ReceiptProvider);
    public bool HasAuthenticationProfile => AuthenticationProfile is not null;
    public bool CanChooseAuthentication => AuthenticationProfile is { SupportsAnonymous: true, SupportsCredentials: true };
    public bool ShowReceiptCredentials => !UseAnonymous && AuthenticationProfile?.SupportsCredentials == true;
    public bool UseAnonymous
    {
        get => useAnonymous;
        set
        {
            if (!CanChooseAuthentication || useAnonymous == value)
                return;
            useAnonymous = value;
            if (value)
                receiptCredentials = null;
            NotifyAuthentication();
        }
    }
    public ProviderOption[] ReceiptProviders { get; private set; } = [];
    public event EventHandler? CredentialsCleared;

    private bool HasTelegramToken => SupportsCredentials && !string.IsNullOrWhiteSpace(telegramToken);
    private bool AuthenticationChanged => receiptCredentials is not null
        || AuthenticationProfile is { } profile && UseAnonymous != profile.UseAnonymous;
    private bool HasDraft => saved is { } current
        && (TelegramEnabled != current.TelegramEnabled || ReceiptsEnabled != current.ReceiptsEnabled
            || ReceiptProvider != current.ReceiptProvider || telegramToken.Length != 0 || AuthenticationChanged
            || !SameCurrencySettings(ManualCurrency, current.ManualCurrency)
            || TelegramPreferences != current.TelegramPreferences);

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
            if (receiptProvider == value)
                return;
            receiptProvider = value;
            ResetAuthenticationDraft();
            OnPropertyChanged(nameof(ReceiptProvider));
        }
    }

    public string Status { get => status; private set => SetField(ref status, value); }

    public void EnterTelegramToken(string value)
    {
        telegramToken = SupportsCredentials && active ? value : "";
        OnPropertyChanged(nameof(CanSave));
    }

    public void SetReceiptCredentials(ReceiptAuthenticationProfileDto profile, Dictionary<string, string> values)
    {
        if (!CanEdit || !ShowReceiptCredentials || !ReferenceEquals(profile, AuthenticationProfile))
            return;
        receiptCredentials = values;
        OnPropertyChanged(nameof(CanSave));
    }

    public void SetManualCurrency(ManualCurrencySettingsDto original, ManualCurrencySettingsDto updated)
    {
        if (CanEdit && ReferenceEquals(ManualCurrency, original) && !SameCurrencySettings(original, updated))
            ManualCurrency = updated;
    }

    public void SetTelegramPreferences(TelegramPreferencesDto original, TelegramPreferencesDto updated)
    {
        if (CanEdit && ReferenceEquals(TelegramPreferences, original))
            TelegramPreferences = updated;
    }

    private static bool SameCurrencySettings(ManualCurrencySettingsDto? first, ManualCurrencySettingsDto? second) =>
        ReferenceEquals(first, second) || first is not null && second is not null
        && first.DefaultCurrency == second.DefaultCurrency
        && first.Currencies.ToHashSet(StringComparer.Ordinal).SetEquals(second.Currencies);

    public Task OpenAsync(string address, string? protectedKey)
    {
        active = true;
        (string Address, string Key)? next = string.IsNullOrEmpty(protectedKey) ? null : (address, protectedKey);
        if (connection != next)
        {
            CancelRequest();
            ClearCredentials();
            connection = next;
            pendingRefresh = false;
            pendingRevision = null;
            OnPropertyChanged(nameof(HasConnection));
            ApplySettings(null);
            Status = "";
        }

        NotifyActions();
        var settings = saved is null || recheckAfterSave ? ReloadAsync() : RefreshAfterNotificationAsync();
        return Task.WhenAll(settings, Updates.OpenAsync(address, protectedKey));
    }

    public void Close()
    {
        active = false;
        Updates.Close();
        // The server may have committed before the client stopped waiting for its response.
        if (saving)
            recheckAfterSave = true;
        if (request is not null)
            Status = "";
        CancelRequest();
        ClearCredentials();
        NotifyActions();
    }

    public Task ReloadAsync() => CanReload
        ? RunAsync(server.GetServerSettingsAsync, "Загрузка…")
        : Task.CompletedTask;

    public Task MarkChangedAsync(long? revision = null)
    {
        if (revision is { } version && saved?.Revision >= version)
            return Task.CompletedTask;

        pendingRefresh = true;
        pendingRevision = revision is null ? null : Math.Max(pendingRevision ?? revision.Value, revision.Value);
        return RefreshAfterNotificationAsync();
    }

    private Task RefreshAfterNotificationAsync()
    {
        if (!pendingRefresh || !CanReload)
            return Task.CompletedTask;
        if (!HasDraft)
            return ReloadAsync();

        requiresReload = true;
        Status = "Настройки изменились на сервере. Обновите их перед сохранением.";
        NotifyActions();
        return Task.CompletedTask;
    }

    public Task SaveAsync()
    {
        if (!CanSave || saved is null || ReceiptProvider is null)
            return Task.CompletedTask;

        var legacy = saved.ReceiptAuthenticationProfiles is null;
        var update = new ServerSettingsWriteDto(TelegramEnabled, ReceiptsEnabled, ReceiptProvider, saved.Revision)
        {
            TelegramToken = HasTelegramToken ? telegramToken.Trim() : null,
            NalogCredentials = legacy && ReceiptProvider == "nalog" && receiptCredentials is { } credentials
                ? new NalogCredentialsWriteDto(credentials["inn"].Trim(), credentials["password"]) : null,
            ReceiptAuthentication = !legacy && AuthenticationChanged
                ? new ReceiptAuthenticationWriteDto(UseAnonymous, receiptCredentials) : null,
            ManualCurrency = SameCurrencySettings(ManualCurrency, saved.ManualCurrency) ? null : ManualCurrency,
            TelegramPreferences = TelegramPreferences == saved.TelegramPreferences ? null : TelegramPreferences
        };
        return RunAsync(token => server.SaveServerSettingsAsync(update, token), "Сохранение…", isSave: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<ServerSettingsDto>> operation, string progress, bool isSave = false)
    {
        using var current = new CancellationTokenSource();
        request = current;
        saving = isSave;
        pendingRefresh = false;
        pendingRevision = null;
        Status = progress;
        NotifyActions();
        try
        {
            var result = await operation(current.Token);
            if (request == current)
            {
                ApplySettings(result);
                if (pendingRevision is { } version && result.Revision >= version)
                {
                    pendingRefresh = false;
                    pendingRevision = null;
                }
                if (isSave)
                    ClearCredentials();
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
                _ = RefreshAfterNotificationAsync();
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
        if (!SupportsCredentials)
            ClearCredentials();
        requiresReload = false;
        recheckAfterSave = false;
        ManualCurrency = value?.ManualCurrency;
        TelegramPreferences = value?.TelegramPreferences;
        TelegramEnabled = value?.TelegramEnabled ?? false;
        ReceiptsEnabled = value?.ReceiptsEnabled ?? false;
        ReceiptProviders = value?.ReceiptProviders.Select(id => new ProviderOption(id, id switch
        {
            "serbia" => "Налоговая Сербии",
            "nalog" => "ФНС России",
            _ => id
        })).ToArray() ?? [];
        OnPropertyChanged(nameof(ReceiptProviders));
        authenticationProfiles = value?.ReceiptAuthenticationProfiles ?? LegacyProfiles(value);
        receiptProvider = value?.ReceiptProvider;
        ResetAuthenticationDraft();
        OnPropertyChanged(nameof(ReceiptProvider));
        OnPropertyChanged(nameof(TelegramStatus));
        OnPropertyChanged(nameof(SupportsCredentials));
        OnPropertyChanged(nameof(SupportsManualCurrency));
        OnPropertyChanged(nameof(SavedManualCurrency));
        OnPropertyChanged(nameof(SupportsTelegramPreferences));
    }

    private static ReceiptAuthenticationProfileDto[] LegacyProfiles(ServerSettingsDto? value) =>
        value?.ReceiptProviders.Select(id => id switch
        {
            "serbia" => new ReceiptAuthenticationProfileDto(id, true, false, true, false, []),
            "nalog" when value.NalogConfigured is { } configured =>
                new ReceiptAuthenticationProfileDto(id, false, true, false, configured,
                    [new("inn", "ИНН", true, true), new("password", "Пароль", true, true)]),
            _ => null
        }).OfType<ReceiptAuthenticationProfileDto>().ToArray() ?? [];

    private void ResetAuthenticationDraft()
    {
        receiptCredentials = null;
        useAnonymous = AuthenticationProfile?.UseAnonymous ?? false;
        NotifyAuthentication();
    }

    private void NotifyAuthentication()
    {
        OnPropertyChanged(nameof(AuthenticationProfile));
        OnPropertyChanged(nameof(HasAuthenticationProfile));
        OnPropertyChanged(nameof(CanChooseAuthentication));
        OnPropertyChanged(nameof(ShowReceiptCredentials));
        OnPropertyChanged(nameof(UseAnonymous));
        OnPropertyChanged(nameof(CanSave));
    }

    private void ClearCredentials()
    {
        if (telegramToken.Length != 0)
        {
            telegramToken = "";
            CredentialsCleared?.Invoke(this, EventArgs.Empty);
        }
        ResetAuthenticationDraft();
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
