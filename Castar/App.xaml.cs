using Castar.Api;
using Castar.Compontents;
using Castar.Contents;
using Castar.Currency;
using Castar.Logger;
using Castar.Notifications;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Castar;

public partial class App : Application
{
    private const string AutorunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string LogPath = Path.Combine(ClientSettings.DataDirectory, "Logs", "castar.log");
    private readonly ICLogger cLogger = new CLogger(LogPath);
    private ServiceProvider? serviceProvider;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        cLogger.Log<App>($"Запуск Castar {version}.", TypeLog.Info);
        base.OnStartup(e);
        TabNavigationBlock.Install();
        InitializeCultures();

        ClientSettings settings;
        var settingsReadFailed = false;
        try
        {
            settings = ClientSettings.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            settingsReadFailed = true;
            settings = new ClientSettings();
        }

        var services = new ServiceCollection();
        services.AddSingleton(cLogger);
        services.AddSingleton(settings);
        services.AddSingleton<CastarServerClient>();
        services.AddSingleton<HistoricalExchangeRates>();
        services.AddSingleton<BudgetEventConnection>();
        services.AddSingleton<Updates.GitHubUpdateService>();
        services.AddSingleton<ITrayIcon>(_ => new TrayIcon(
            Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, "Castar",
            (_, _) =>
            {
                MainWindow.Show();
                MainWindow.Activate();
            }));
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsContent>();
        services.AddSingleton<IncomesContent>();
        services.AddSingleton<PurchasesContent>();
        services.AddSingleton<BudgetStatistics>();
        services.AddSingleton(provider => new StatisticsContent(provider.GetRequiredService<BudgetStatistics>(),
            provider.GetRequiredService<CastarServerClient>(), settings));
        services.AddSingleton(provider => new Lazy<StatisticsContent>(provider.GetRequiredService<StatisticsContent>));
        services.AddSingleton(provider =>
        {
            var window = provider.GetRequiredService<MainWindow>();
            return new CastarNotificationCenter(window, window.ShowBudgetNotification,
                provider.GetRequiredService<CastarServerClient>().GetUserAvatarAsync);
        });

        serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var tray = serviceProvider.GetRequiredService<ITrayIcon>();
        var notifications = serviceProvider.GetRequiredService<CastarNotificationCenter>();
        notifications.Configure(settings.NotificationCorner, settings.MaxVisibleNotifications);
        settings.ConnectionChanged += (_, _) =>
        {
            notifications.Clear();
            serviceProvider.GetRequiredService<CastarServerClient>().ClearAvatarCache();
        };
        ApplyPreferences(settings, tray);
        Task<bool?>? settingsSaveWarning = null;
        settings.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ClientSettings.NotificationMode))
                notifications.Clear();
            if (change.PropertyName is nameof(ClientSettings.NotificationCorner) or nameof(ClientSettings.MaxVisibleNotifications))
                notifications.Configure(settings.NotificationCorner, settings.MaxVisibleNotifications);
            try
            {
                settings.Save();
                if (change.PropertyName is nameof(ClientSettings.IsHideInTray) or nameof(ClientSettings.IsAutoRun))
                    ApplyPreferences(settings, tray);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (settingsSaveWarning?.IsCompleted != false)
                    settingsSaveWarning = ShowSettingsWarning("Не удалось сохранить настройки.");
            }
        };

        MainWindow = serviceProvider.GetRequiredService<MainWindow>();
        _ = serviceProvider.GetRequiredService<SettingsContent>().PreloadAsync();
        MainWindow.Show();
        cLogger.Log<App>("Главное окно открыто.", TypeLog.Info);
        if (settingsReadFailed)
            _ = ShowSettingsWarning("Не удалось прочитать настройки. Укажите адрес и ключ сервера.");

        var budgetEvents = serviceProvider.GetRequiredService<BudgetEventConnection>();
        var purchases = serviceProvider.GetRequiredService<PurchasesContent>();
        var incomes = serviceProvider.GetRequiredService<IncomesContent>();
        var statistics = serviceProvider.GetRequiredService<Lazy<StatisticsContent>>();
        void RefreshOpenStatistics()
        {
            if (statistics.IsValueCreated)
                _ = statistics.Value.RefreshAfterServerChangeAsync();
        }

        budgetEvents.Changed += change =>
        {
            switch (change.Kind)
            {
                case BudgetEvents.Purchase:
                    _ = purchases.RefreshAfterServerChangeAsync();
                    break;
                case BudgetEvents.Income:
                    _ = incomes.RefreshAfterServerChangeAsync();
                    break;
                default:
                    return;
            }
            if (change.Created)
            {
                if (settings.NotificationMode == NotificationMode.Castar)
                    notifications.Publish(change with
                    {
                        AddedOn = serviceProvider.GetRequiredService<CastarServerClient>()
                            .DisplayOperationDate(change.AddedOn, change.AddedOnUtc)
                    });
                else
                    tray.Notify(BudgetNotificationText.Format(change.Kind, Math.Max(1, change.CreatedCount), change.Preview));
            }
            RefreshOpenStatistics();
        };
        budgetEvents.Synchronized += () =>
        {
            serviceProvider.GetRequiredService<CastarServerClient>().ClearAvatarCache();
            _ = purchases.RefreshAfterServerChangeAsync();
            _ = incomes.RefreshAfterServerChangeAsync();
            RefreshOpenStatistics();
        };
        budgetEvents.ConfigurationChanged += change =>
        {
            if (change.Kind == ServerSynchronization.Users)
            {
                serviceProvider.GetRequiredService<CastarServerClient>().ClearAvatarCache();
                _ = purchases.RefreshAfterServerChangeAsync();
                _ = incomes.RefreshAfterServerChangeAsync();
            }
        };
        budgetEvents.Start(new DispatcherSynchronizationContext(Dispatcher, DispatcherPriority.Background));
    }

    private static Task<bool?> ShowSettingsWarning(string message) =>
        PopupDialog.Show(message, PopupDialog.TypeDialogEnum.Default, options =>
        {
            options.Title = "Настройки";
            return options;
        });

    private static void ApplyPreferences(ClientSettings settings, ITrayIcon tray)
    {
        if (settings.IsHideInTray)
            tray.Show();
        else
            tray.Dispose();

        if (ClientSettings.IsLocalPreview)
            return;

        using var registry = Registry.CurrentUser.OpenSubKey(AutorunKey, writable: true);
        if (settings.IsAutoRun)
            registry?.SetValue("Castar", $"\"{Environment.ProcessPath}\"");
        else
            registry?.DeleteValue("Castar", throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            serviceProvider?.Dispose();
        }
        finally
        {
            cLogger.Log<App>($"Завершение Castar. Код {e.ApplicationExitCode}.", TypeLog.Info);
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            base.OnExit(e);
        }
    }
}
