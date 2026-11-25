using Castar.Compontents;
using Castar.Contents;
using Castar.Logger;
using Castar.Notifications;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    private Mutex? categorizationInstance;
    private ITrayIcon? categorizationTray;
#if !DEBUG
    private Mutex? windowInstance;
    private EventWaitHandle? windowActivation;
    private RegisteredWaitHandle? windowActivationWait;
#endif

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var isCategorizationAgent = Array.Exists(e.Args, arg => arg == "--categorization-agent");
#if !DEBUG
        if (!isCategorizationAgent && !TryStartWindowInstance())
        {
            Shutdown();
            return;
        }
#endif
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        cLogger.Log<App>($"Запуск Castar {version}.", TypeLog.Info);
        base.OnStartup(e);
        FieldTabNavigation.Install();
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

        serviceProvider = CreateClientServices(settings);
        if (isCategorizationAgent)
        {
            StartCategorizationAgent(settings);
            return;
        }

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
#if !DEBUG
        if (windowActivation is not null)
        {
            windowActivationWait = ThreadPool.RegisterWaitForSingleObject(windowActivation,
                (_, _) => Dispatcher.InvokeAsync(RestoreFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        }
#endif
        cLogger.Log<App>("Главное окно открыто.", TypeLog.Info);
        if (settingsReadFailed)
            _ = ShowSettingsWarning("Не удалось прочитать настройки. Укажите адрес и ключ сервера.");

        var budgetEvents = AttachBudgetSynchronization(serviceProvider, settings, tray, notifications);
        TryStartCategorization();
        budgetEvents.Start(new DispatcherSynchronizationContext(Dispatcher, DispatcherPriority.Background));
    }

    private void StartCategorizationAgent(ClientSettings settings)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!settings.AutomaticCategorizationEnabled || settings.ApiKey.Length == 0)
        {
            cLogger.Log<App>("Для агента категоризации включите классификацию и подключите клиент к Castar Server.", TypeLog.Warning);
            Shutdown(1);
            return;
        }
        if (!TryStartCategorization())
        {
            Shutdown();
            return;
        }

        categorizationTray = CreateTray("Castar — остановить категоризацию", Shutdown, open: null, Shutdown);
        categorizationTray.Show();
        serviceProvider!.GetRequiredService<BudgetEventConnection>()
            .Start(new DispatcherSynchronizationContext(Dispatcher, DispatcherPriority.Background));
    }

    private TrayIcon CreateTray(string tooltip, Action click, Action? open, Action exit)
    {
        var menu = new TrayContextMenu(open, exit);
        return new TrayIcon(Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, tooltip,
            (_, _) => Dispatcher.InvokeAsync(click), (_, _) => Dispatcher.InvokeAsync(menu.ShowAtPointer));
    }

    private void RestoreFromTray() => (MainWindow as MainWindow)?.RestoreAndActivate();

    private void ExitFromTray() => (MainWindow as MainWindow)?.Exit();

#if !DEBUG
    private bool TryStartWindowInstance()
    {
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientSettings.DataDirectory.ToUpperInvariant())));
        var name = $@"Local\Castar.Window.{profile}";
        windowActivation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        var instance = new Mutex(false, name);
        try
        {
            if (!instance.WaitOne(0))
            {
                instance.Dispose();
                windowActivation.Set();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // This process now owns the lock.
        }
        windowInstance = instance;
        return true;
    }
#endif

    private bool TryStartCategorization()
    {
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientSettings.DataDirectory.ToUpperInvariant())));
        var instance = new Mutex(false, $@"Local\Castar.Categorization.{profile}");
        try
        {
            if (!instance.WaitOne(0))
            {
                instance.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // This process now owns the lock.
        }
        categorizationInstance = instance;
        serviceProvider!.GetRequiredService<ExpenseCategorizationService>().Start();
        return true;
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
            categorizationTray?.Dispose();
            serviceProvider?.Dispose();
        }
        finally
        {
#if !DEBUG
            windowActivationWait?.Unregister(null);
            windowActivation?.Dispose();
            windowInstance?.ReleaseMutex();
            windowInstance?.Dispose();
#endif
            categorizationInstance?.ReleaseMutex();
            categorizationInstance?.Dispose();
            cLogger.Log<App>($"Завершение Castar. Код {e.ApplicationExitCode}.", TypeLog.Info);
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            base.OnExit(e);
        }
    }
}
