using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.LocalAi;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Updates;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Castar.Contents;

public partial class SettingsContent : UserControl
{
    private readonly ClientSettings settings;
    private readonly CastarServerClient server;
    private readonly BudgetEventConnection? events;
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly SettingsContext context;
    private CancellationTokenSource? usersRequest;
    private bool usersReloadPending;
    private bool preloadStarted;
    private bool checkingConnection;
    private bool editingUser;
    private string savedConnectionStatus = "";
    private string pendingKey = "";
    private PasswordBox? telegramTokenInput;

    public SettingsContent(ClientSettings settings, CastarServerClient server, GitHubUpdateService updateService,
        HistoricalExchangeRates exchangeRates, BudgetEventConnection? events = null, LocalAiService? localAi = null)
    {
        this.settings = settings;
        this.server = server;
        this.events = events;
        this.updateService = updateService;
        this.exchangeRates = exchangeRates;
        context = new SettingsContext(settings, new ServerSettingsViewModel(server));
        context.ServerConfiguration.CredentialsCleared += (_, _) => telegramTokenInput?.Clear();
        context.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SettingsContext.ServerAddress))
                RefreshConnectionStatus();
        };
        context.ServerConfiguration.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ServerSettingsViewModel.SavedManualCurrency))
                context.RefreshCurrencyOptions();
        };
        settings.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ClientSettings.DisplayCurrency))
                context.RefreshCurrencyOptions();
        };
        context.RefreshCurrencyOptions();
        server.ConnectionChanged += Server_ConnectionChanged;
        if (events is not null)
        {
            events.ConfigurationChanged += Configuration_Changed;
            events.Synchronized += ConnectionSynchronized;
            events.Disconnected += ConnectionLost;
        }
        DataContext = context;
        InitializeComponent();
        if (localAi is null)
            LocalAiSectionButton.Visibility = Visibility.Collapsed;
        else
            LocalAiSettingsHost.Content = new LocalAiSettingsContent(settings, localAi);
    }

    internal Task PreloadAsync()
    {
        if (preloadStarted)
            return Task.CompletedTask;

        preloadStarted = true;
        return Task.WhenAll(LoadServerSettingsAsync(), LoadUsersAsync(verifyConnection: events?.IsSynchronized != true));
    }

    internal void CloseScreen()
    {
        backupRequest?.Cancel();
        BackToSectionMenu();
        ApiKeyInput.Clear();
        context.IsApiKeyEditorVisible = false;
        context.ServerConfiguration.ClearSensitiveDraft();
    }

    internal Task RetryUnavailableDataAsync()
    {
        if (!preloadStarted)
            return PreloadAsync();
        if (settings.ApiKey.Length == 0)
            return Task.CompletedTask;

        return Task.WhenAll(
            context.ServerConfiguration.HasSettings ? Task.CompletedTask : context.ServerConfiguration.ReloadAsync(),
            context.UsersAvailable && context.UsersStatus.Length == 0 ? Task.CompletedTask : LoadUsersAsync());
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        context.HasSavedConnection = settings.ApiKey.Length != 0;
        backupRequest?.Cancel();
        SetSavedConnectionStatus(context.HasSavedConnection ? CastarServerClient.ConnectionNotEstablished : "");
        ResetUsers();
        if (preloadStarted)
            await Task.WhenAll(LoadServerSettingsAsync(), LoadUsersAsync(verifyConnection: !checkingConnection));
    }

    private async void ConnectionSynchronized()
    {
        SetSavedConnectionStatus("");
        await Task.WhenAll(context.ServerConfiguration.MarkChangedAsync(), LoadUsersAsync(verifyConnection: false));
    }

    private void ConnectionLost()
    {
        SetSavedConnectionStatus("Канал событий недоступен. Ожидание восстановления связи.");
    }

    private async void Configuration_Changed(ServerSynchronizationDto change)
    {
        switch (change.Kind)
        {
            case ServerSynchronization.Users:
                await LoadUsersAsync(verifyConnection: false);
                break;
            case ServerSynchronization.Settings:
                await context.ServerConfiguration.MarkChangedAsync(change.Revision);
                break;
        }
    }

    private Task LoadServerSettingsAsync() =>
        context.ServerConfiguration.OpenAsync(settings.ServerUrl, settings.ProtectedApiKey);

    private async void SaveServerSettings_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, () => context.ServerConfiguration.SaveAsync());

    private async void ReloadServerSettings_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, () => context.ServerConfiguration.ReloadAsync());

    private void ConfigureManualCurrencies_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = context.ServerConfiguration;
        if (!viewModel.CanEdit || viewModel.ManualCurrency is not { } original)
            return;

        var panel = new ManualCurrencyPanel(exchangeRates, original);
        OpenNestedSettings((Button)sender, "Валюты ручного ввода", panel,
            () => viewModel.CanEdit && ReferenceEquals(viewModel.ManualCurrency, original), panel.Validate,
            () => viewModel.SetManualCurrency(original, panel.CreateResult()), panel.CancelCatalog);
    }

    private void TelegramToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        telegramTokenInput = (PasswordBox)sender;
        context.ServerConfiguration.EnterTelegramToken(telegramTokenInput.Password);
    }

    private void ConfigureReceiptCredentials_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = context.ServerConfiguration;
        if (!viewModel.CanEdit || !viewModel.ShowReceiptCredentials || viewModel.AuthenticationProfile is not { } profile)
            return;

        var panel = new ReceiptCredentialsPanel(profile.Fields);
        OpenNestedSettings((Button)sender, "Данные для входа", panel,
            () => viewModel.CanEdit && viewModel.ShowReceiptCredentials && ReferenceEquals(viewModel.AuthenticationProfile, profile),
            panel.Validate, () => viewModel.SetReceiptCredentials(profile, panel.Values));
    }

    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        pendingKey = ((PasswordBox)sender).Password.Trim();
        RefreshConnectionStatus();
    }

    private void RevealApiKey_Click(object sender, RoutedEventArgs e)
    {
        context.IsApiKeyEditorVisible = true;
        ApiKeyInput.Focus();
    }

    private async void DisconnectServer_Click(object sender, RoutedEventArgs e)
    {
        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        if (!context.HasSavedConnection || await PopupDialog.Show("Отключиться от Castar Server?",
            PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.SuccessButton.SuccessConfirmButtonText = "Отключиться";
                options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                return options;
            }) != true || connection != (settings.ServerUrl, settings.ProtectedApiKey))
            return;

        try
        {
            settings.Disconnect();
            ApiKeyInput.Clear();
            context.IsApiKeyEditorVisible = false;
            context.ConnectionStatus = "";
            RevealApiKeyButton.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            context.ConnectionStatus = "Не удалось сохранить настройки подключения.";
        }
    }

    private async void SaveServerConnection_Click(object sender, RoutedEventArgs e) =>
        await ButtonLoading.RunAsync(sender, async () =>
        {
            checkingConnection = true;
            var addressInput = context.ServerAddress;
            var enteredKey = pendingKey;
            bool IsCurrentInput() => context.ServerAddress == addressInput && pendingKey == enteredKey;
            Uri? address = null;
            try
            {
                address = CastarServerClient.ParseServerUrl(addressInput);
                var key = enteredKey;
                if (key.Length == 0 && Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var saved) && saved == address)
                    key = settings.ApiKey;

                context.ConnectionStatus = "Проверка подключения…";
                await server.TestConnectionAsync(address, key);
                if (!IsCurrentInput())
                    return;

                if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var previous) ||
                    previous != address || settings.ApiKey != key)
                    settings.SaveConnection(address, key);
                else
                    _ = Task.WhenAll(context.ServerConfiguration.MarkChangedAsync(), LoadUsersAsync(verifyConnection: false));
                ApiKeyInput.Clear();
                pendingKey = "";
                context.IsApiKeyEditorVisible = false;
                savedConnectionStatus = "";
                context.ConnectionStatus = "";
                DisconnectServerButton.Focus();
            }
            catch (Exception error) when (CastarServerClient.IsConnectionError(error))
            {
                if (IsCurrentInput())
                    context.ConnectionStatus = CastarServerClient.DescribeConnectionFailure(error, address);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            {
                context.ConnectionStatus = "Не удалось сохранить настройки подключения.";
            }
            finally
            {
                checkingConnection = false;
                if (!IsCurrentInput())
                    RefreshConnectionStatus();
            }
        });

    private bool DisplaysSavedConnection => pendingKey.Length == 0
        && Uri.TryCreate(context.ServerAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var shown)
        && Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var saved)
        && shown == saved;

    private void RefreshConnectionStatus()
    {
        if (!checkingConnection)
            context.ConnectionStatus = DisplaysSavedConnection
                ? savedConnectionStatus : "";
    }

    private void SetSavedConnectionStatus(string status)
    {
        savedConnectionStatus = status;
        if (!checkingConnection && DisplaysSavedConnection)
            context.ConnectionStatus = status;
    }

    private async Task LoadUsersAsync(bool verifyConnection = true)
    {
        if (!preloadStarted)
            return;
        if (usersRequest is not null)
        {
            if (!verifyConnection)
                usersReloadPending = true;
            return;
        }

        var key = settings.ApiKey;
        if (key.Length == 0)
        {
            SetSavedConnectionStatus("");
            return;
        }

        using var current = new CancellationTokenSource();
        usersRequest = current;
        Uri? address = null;
        var verifiedConnection = !verifyConnection;
        try
        {
            if (verifyConnection)
            {
                address = CastarServerClient.ParseServerUrl(settings.ServerUrl);
                await server.TestConnectionAsync(address, key, current.Token);
                if (usersRequest != current)
                    return;
                verifiedConnection = true;
            }
            SetSavedConnectionStatus("");
            var users = await server.GetUsersAsync(current.Token);
            if (usersRequest != current)
                return;

            if (context.Users.Count != users.Count || !context.Users.Zip(users).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name
                && pair.First.TelegramId == pair.Second.TelegramId && pair.First.Sex == pair.Second.Sex
                && pair.First.TelegramNotificationsEnabled == pair.Second.TelegramNotificationsEnabled
                && pair.First.ColorRaw.AsSpan().SequenceEqual(pair.Second.ColorRaw)
                && pair.First.ImageRaw.AsSpan().SequenceEqual(pair.Second.ImageRaw)))
            {
                context.Users = users;
            }
            context.UsersAvailable = true;
            context.UsersStatus = "";
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (usersRequest == current)
            {
                if (!verifiedConnection || error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                    SetSavedConnectionStatus(CastarServerClient.DescribeConnectionFailure(error, address));
                if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                {
                    context.UsersAvailable = false;
                    context.Users = [];
                }
                context.UsersStatus = CastarServerClient.DescribeError(error);
            }
        }
        finally
        {
            if (usersRequest == current)
            {
                usersRequest = null;
                if (usersReloadPending)
                {
                    usersReloadPending = false;
                    _ = LoadUsersAsync(verifyConnection: false);
                }
            }
        }
    }

    private void ResetUsers()
    {
        var previous = usersRequest;
        usersRequest = null;
        usersReloadPending = false;
        previous?.Cancel();
        context.UsersAvailable = false;
        context.UsersStatus = CastarServerClient.ConnectionNotEstablished;
        if (context.Users.Count > 0)
            context.Users = [];
    }

    private async void UsersListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ((ListView)sender).SelectedValue is User user)
            await EditUserAsync(user);
    }

    private async void NewUserButton_Click(object sender, RoutedEventArgs e) => await EditUserAsync(new User(), sender);

    private async Task EditUserAsync(User user, object? source = null)
    {
        if (!context.UsersAvailable || editingUser)
            return;

        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        bool IsCurrent() => IsVisible && context.UsersAvailable && connection == (settings.ServerUrl, settings.ProtectedApiKey);
        var isNew = user.Id == Guid.Empty;
        editingUser = true;
        try
        {
            var canSetTelegramNotifications = false;
            if (!isNew)
            {
                await ButtonLoading.RunAsync(source, async () =>
                    (user, canSetTelegramNotifications) = await server.GetUserForEditingAsync(user.Id));
                if (!IsCurrent())
                    return;
            }

            var initialNotifications = user.TelegramNotificationsEnabled;
            var editor = new UserInfoPanel
            {
                IsEditContent = true,
                CanSetTelegramNotifications = canSetTelegramNotifications,
                SizeImage = 100,
                DataContext = user
            };
            var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                options.SuccessButton.SuccessConfirmButtonText = isNew ? "Создать" : "Сохранить";
                options.AllowMissClick = false;
                options.Title = isNew ? "Новый пользователь" : "Изменить пользователя";
                return options;
            });
            if (accepted != true || !IsCurrent())
                return;

            user.Name = string.IsNullOrWhiteSpace(user.Name) ? "NONAME" : user.Name.Trim();
            await ButtonLoading.RunAsync(source, async () =>
            {
                if (isNew)
                    await server.AddUserAsync(user);
                else
                    await server.UpdateUserAsync(user, canSetTelegramNotifications &&
                        user.TelegramNotificationsEnabled != initialNotifications);
                if (events is not { IsSynchronized: true, SupportsConfigurationEvents: true })
                    await LoadUsersAsync(verifyConnection: false);
            });
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (IsCurrent())
                await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
        finally
        {
            editingUser = false;
        }
    }

    private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)((MenuItem)sender).Parent;
        if (!context.UsersAvailable || ((ListView)menu.PlacementTarget).SelectedItem is not User user)
            return;

        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        var accepted = await PopupDialog.Show($"Удалить «{user.Name}»?",
            PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.SuccessButton.SuccessConfirmButtonText = "Удалить";
                options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                return options;
            });
        if (accepted != true || !context.UsersAvailable || connection != (settings.ServerUrl, settings.ProtectedApiKey))
            return;
        try
        {
            await ButtonLoading.RunAsync(sender, async () =>
            {
                await server.DeleteUserAsync(user.Id);
                if (events is not { IsSynchronized: true, SupportsConfigurationEvents: true })
                    await LoadUsersAsync(verifyConnection: false);
            });
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
    }

    private sealed class SettingsContext(ClientSettings settings, ServerSettingsViewModel serverConfiguration) : BaseSettingsModel
    {
        private string address = settings.ServerUrl;
        private string connectionStatus = "";
        private bool hasSavedConnection = settings.ApiKey.Length != 0;
        private bool isApiKeyEditorVisible;
        private bool clientUpdateAvailable;
        private bool isInstallingClientUpdate;
        private string updateStatus = "";
        private int updateDownloadPercent;
        private bool isDownloadingUpdate;
        private bool usersAvailable;
        private string usersStatus = "";
        private ObservableCollection<User> users = [];

        public ClientSettings Settings { get; } = settings;
        public ObservableCollection<CurrencyOption> CurrencyOptions { get; } = [new("", "Исходная валюта")];
        public NotificationOption<NotificationMode>[] NotificationModes { get; } =
        [
            new(NotificationMode.Windows, "Системные Windows"),
            new(NotificationMode.Castar, "Castar")
        ];
        public NotificationOption<NotificationCorner>[] NotificationCorners { get; } =
        [
            new(NotificationCorner.TopLeft, "Сверху слева"),
            new(NotificationCorner.TopRight, "Сверху справа"),
            new(NotificationCorner.BottomLeft, "Снизу слева"),
            new(NotificationCorner.BottomRight, "Снизу справа")
        ];
        public int[] NotificationCounts { get; } = [1, 2, 3, 4, 5];

        public void RefreshCurrencyOptions()
        {
            var configured = ServerConfiguration.SavedManualCurrency?.Currencies ?? HistoricalExchangeRates.SupportedCurrencies;
            var codes = configured.Append(Settings.DisplayCurrency).Where(HistoricalExchangeRates.IsCurrencyCode)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var code in codes)
            {
                if (CurrencyOptions.Any(option => option.Code == code))
                    continue;
                var index = 1 + CurrencyOptions.Skip(1).TakeWhile(option => string.CompareOrdinal(option.Code, code) < 0).Count();
                CurrencyOptions.Insert(index, new CurrencyOption(code, code));
            }

            foreach (var option in CurrencyOptions.Skip(1).Where(option => !codes.Contains(option.Code)).ToArray())
                CurrencyOptions.Remove(option);
        }
        public ServerSettingsViewModel ServerConfiguration { get; } = serverConfiguration;
        public string ServerAddress { get => address; set => SetField(ref address, value); }
        public string ConnectionStatus { get => connectionStatus; set => SetField(ref connectionStatus, value); }
        public bool HasSavedConnection { get => hasSavedConnection; set => SetField(ref hasSavedConnection, value); }
        public bool IsApiKeyEditorVisible { get => isApiKeyEditorVisible; set => SetField(ref isApiKeyEditorVisible, value); }
        public bool ClientUpdateAvailable { get => clientUpdateAvailable; set => SetField(ref clientUpdateAvailable, value); }
        public bool IsInstallingClientUpdate
        {
            get => isInstallingClientUpdate;
            set
            {
                if (SetField(ref isInstallingClientUpdate, value))
                    OnPropertyChanged(nameof(CanInstallClientUpdate));
            }
        }
        public bool CanInstallClientUpdate => !ClientSettings.IsLocalPreview && !IsInstallingClientUpdate;
        public string UpdateStatus { get => updateStatus; set => SetField(ref updateStatus, value); }
        public int UpdateDownloadPercent { get => updateDownloadPercent; set => SetField(ref updateDownloadPercent, value); }
        public bool IsDownloadingUpdate { get => isDownloadingUpdate; set => SetField(ref isDownloadingUpdate, value); }
        public bool UsersAvailable { get => usersAvailable; set => SetField(ref usersAvailable, value); }
        public string UsersStatus { get => usersStatus; set => SetField(ref usersStatus, value); }
        public ObservableCollection<User> Users { get => users; set => SetField(ref users, value); }
    }

    private sealed record CurrencyOption(string Code, string Label);
    private sealed record NotificationOption<T>(T Value, string Label);
}
