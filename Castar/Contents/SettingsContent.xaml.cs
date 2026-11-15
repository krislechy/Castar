using Castar.Compontents;
using Castar.Currency;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Updates;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Castar.Contents;

public partial class SettingsContent : UserControl
{
    private const string Connected = "Соединение установлено.";
    private readonly ClientSettings settings;
    private readonly CastarServerClient server;
    private readonly SettingsContext context;
    private readonly DispatcherTimer polling;
    private CancellationTokenSource? usersRequest;
    private bool showingServerSettings;
    private bool checkingConnection;
    private string savedConnectionStatus = CastarServerClient.ConnectionNotEstablished;
    private string pendingKey = "";
    private PasswordBox? keyInput;
    private PasswordBox? telegramTokenInput;
    private PasswordBox? nalogInnInput;
    private PasswordBox? nalogPasswordInput;

    public SettingsContent(ClientSettings settings, CastarServerClient server, GitHubUpdateService updateService)
    {
        this.settings = settings;
        this.server = server;
        this.updateService = updateService;
        context = new SettingsContext(settings, new ServerSettingsViewModel(server))
        {
            UpdateStatus = $"Текущая версия {updateService.InstalledVersion}"
        };
        context.ServerConfiguration.CredentialsCleared += (_, _) =>
        {
            telegramTokenInput?.Clear();
            nalogInnInput?.Clear();
            nalogPasswordInput?.Clear();
        };
        context.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SettingsContext.ServerAddress))
                RefreshConnectionStatus();
        };
        polling = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        polling.Tick += async (_, _) =>
        {
            if (IsVisible)
                await LoadUsersAsync();
        };
        DataContext = context;
        InitializeComponent();
    }

    private async void Settings_Loaded(object sender, RoutedEventArgs e)
    {
        polling.Start();
        await OpenServerSettingsAsync();
    }

    private void Settings_Unloaded(object sender, RoutedEventArgs e)
    {
        polling.Stop();
        CloseServerSettings();
    }

    private async void Settings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            await OpenServerSettingsAsync();
        else
            CloseServerSettings();
    }

    private Task OpenServerSettingsAsync()
    {
        if (!IsLoaded || !IsVisible || showingServerSettings)
            return Task.CompletedTask;

        showingServerSettings = true;
        server.ConnectionChanged += Server_ConnectionChanged;
        return Task.WhenAll(LoadServerSettingsAsync(), LoadUsersAsync());
    }

    private void CloseServerSettings()
    {
        if (!showingServerSettings)
            return;

        showingServerSettings = false;
        server.ConnectionChanged -= Server_ConnectionChanged;
        context.ServerConfiguration.Close();
        ResetUsers();
        SetSavedConnectionStatus(CastarServerClient.ConnectionNotEstablished);
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        ResetUsers();
        await Task.WhenAll(LoadServerSettingsAsync(), LoadUsersAsync());
    }

    private Task LoadServerSettingsAsync() =>
        context.ServerConfiguration.OpenAsync(settings.ServerUrl, settings.ProtectedApiKey);

    private async void SaveServerSettings_Click(object sender, RoutedEventArgs e) =>
        await context.ServerConfiguration.SaveAsync();

    private async void ReloadServerSettings_Click(object sender, RoutedEventArgs e) =>
        await context.ServerConfiguration.ReloadAsync();

    private async void UpdateServer_Click(object sender, RoutedEventArgs e) =>
        await context.ServerConfiguration.Updates.StartAsync();

    private void TelegramToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        telegramTokenInput = (PasswordBox)sender;
        context.ServerConfiguration.EnterTelegramToken(telegramTokenInput.Password);
    }

    private void NalogInn_PasswordChanged(object sender, RoutedEventArgs e)
    {
        nalogInnInput = (PasswordBox)sender;
        context.ServerConfiguration.EnterNalogInn(nalogInnInput.Password);
    }

    private void NalogPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        nalogPasswordInput = (PasswordBox)sender;
        context.ServerConfiguration.EnterNalogPassword(nalogPasswordInput.Password);
    }

    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        keyInput = (PasswordBox)sender;
        pendingKey = keyInput.Password.Trim();
        RefreshConnectionStatus();
    }

    private async void SaveServerConnection_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
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

            settings.SaveConnection(address, key);
            keyInput?.Clear();
            pendingKey = "";
            savedConnectionStatus = Connected;
            context.ConnectionStatus = Connected;
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
            button.IsEnabled = true;
        }
    }

    private bool DisplaysSavedConnection => pendingKey.Length == 0
        && Uri.TryCreate(context.ServerAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var shown)
        && Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var saved)
        && shown == saved;

    private void RefreshConnectionStatus()
    {
        if (!checkingConnection)
            context.ConnectionStatus = DisplaysSavedConnection
                ? savedConnectionStatus : CastarServerClient.ConnectionNotEstablished;
    }

    private void SetSavedConnectionStatus(string status)
    {
        savedConnectionStatus = status;
        if (!checkingConnection && DisplaysSavedConnection)
            context.ConnectionStatus = status;
    }

    private async Task LoadUsersAsync()
    {
        if (!showingServerSettings || usersRequest is not null)
            return;

        var key = settings.ApiKey;
        if (key.Length == 0)
        {
            SetSavedConnectionStatus(CastarServerClient.ConnectionNotEstablished);
            return;
        }

        using var current = new CancellationTokenSource();
        usersRequest = current;
        Uri? address = null;
        var verifiedConnection = false;
        try
        {
            address = CastarServerClient.ParseServerUrl(settings.ServerUrl);
            await server.TestConnectionAsync(address, key, current.Token);
            if (usersRequest != current)
                return;

            verifiedConnection = true;
            SetSavedConnectionStatus(Connected);
            var users = await server.GetUsersAsync(current.Token);
            if (usersRequest != current)
                return;

            if (context.Users.Count != users.Count || !context.Users.Zip(users).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name
                && pair.First.TelegramId == pair.Second.TelegramId && pair.First.Sex == pair.Second.Sex
                && pair.First.ColorRaw.AsSpan().SequenceEqual(pair.Second.ColorRaw)
                && pair.First.ImageRaw.AsSpan().SequenceEqual(pair.Second.ImageRaw)))
            {
                context.Users = users;
            }
            context.UsersAvailable = true;
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (usersRequest == current)
            {
                if (!verifiedConnection)
                    SetSavedConnectionStatus(CastarServerClient.DescribeConnectionFailure(error, address));
                ResetUsers();
            }
        }
        finally
        {
            if (usersRequest == current)
                usersRequest = null;
        }
    }

    private void ResetUsers()
    {
        var previous = usersRequest;
        usersRequest = null;
        previous?.Cancel();
        context.UsersAvailable = false;
        if (context.Users.Count > 0)
            context.Users = [];
    }

    private async void UsersListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((ListView)sender).SelectedValue is User user)
            await EditUserAsync(user.Clone<User>());
    }

    private async void NewUserButton_Click(object sender, RoutedEventArgs e) => await EditUserAsync(new User());

    private async Task EditUserAsync(User user)
    {
        if (!context.UsersAvailable)
            return;

        var connection = (settings.ServerUrl, settings.ProtectedApiKey);

        var isNew = user.Id == Guid.Empty;
        var editor = new UserInfoPanel { IsEditContent = true, SizeImage = 100, DataContext = user };
        var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
        {
            options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
            options.SuccessButton.SuccessConfirmButtonText = isNew ? "Создать" : "Сохранить";
            options.AllowMissClick = false;
            options.Title = isNew ? "Новый пользователь" : "Изменить пользователя";
            return options;
        });
        if (accepted != true || !context.UsersAvailable || connection != (settings.ServerUrl, settings.ProtectedApiKey))
            return;

        user.Name = string.IsNullOrWhiteSpace(user.Name) ? "NONAME" : user.Name.Trim();
        try
        {
            if (isNew)
                await server.AddUserAsync(user);
            else
                await server.UpdateUserAsync(user);
            await LoadUsersAsync();
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
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
            await server.DeleteUserAsync(user.Id);
            await LoadUsersAsync();
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
    }

    private sealed class SettingsContext(ClientSettings settings, ServerSettingsViewModel serverConfiguration) : BaseSettingsModel
    {
        private string address = settings.ServerUrl;
        private string connectionStatus = CastarServerClient.ConnectionNotEstablished;
        private string updateStatus = "";
        private int updateDownloadPercent;
        private bool isDownloadingUpdate;
        private bool usersAvailable;
        private ObservableCollection<User> users = [];

        public ClientSettings Settings { get; } = settings;
        public CurrencyOption[] CurrencyOptions { get; } = HistoricalExchangeRates.SupportedCurrencies
            .Select(code => new CurrencyOption(code, code)).Prepend(new CurrencyOption("", "Исходная валюта")).ToArray();
        public ServerSettingsViewModel ServerConfiguration { get; } = serverConfiguration;
        public string ServerAddress { get => address; set => SetField(ref address, value); }
        public string ConnectionStatus { get => connectionStatus; set => SetField(ref connectionStatus, value); }
        public string UpdateStatus { get => updateStatus; set => SetField(ref updateStatus, value); }
        public int UpdateDownloadPercent { get => updateDownloadPercent; set => SetField(ref updateDownloadPercent, value); }
        public bool IsDownloadingUpdate { get => isDownloadingUpdate; set => SetField(ref isDownloadingUpdate, value); }
        public bool UsersAvailable { get => usersAvailable; set => SetField(ref usersAvailable, value); }
        public ObservableCollection<User> Users { get => users; set => SetField(ref users, value); }
    }

    private sealed record CurrencyOption(string Code, string Label);
}
