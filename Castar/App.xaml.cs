using Castar.Contents;
using Castar.Logger;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Windows;

namespace Castar;

public partial class App : Application
{
    private const string AutorunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ICLogger cLogger = new CLogger(Path.Combine(ClientSettings.DataDirectory, "Logs", "castar.log"));
    private ServiceProvider? serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InitializeCultures();
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        ClientSettings settings;
        try
        {
            settings = ClientSettings.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("Не удалось прочитать настройки клиента Castar. Укажите адрес и ключ сервера.",
                "Castar", MessageBoxButton.OK, MessageBoxImage.Warning);
            settings = new ClientSettings();
        }

        var services = new ServiceCollection();
        services.AddSingleton(cLogger);
        services.AddSingleton(settings);
        services.AddSingleton<CastarServerClient>();
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

        serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var tray = serviceProvider.GetRequiredService<ITrayIcon>();
        ApplyPreferences(settings, tray);
        settings.PropertyChanged += (_, _) =>
        {
            try
            {
                settings.Save();
                ApplyPreferences(settings, tray);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show("Не удалось сохранить настройки Castar.",
                    "Castar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        MainWindow = serviceProvider.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    private static void ApplyPreferences(ClientSettings settings, ITrayIcon tray)
    {
        if (settings.IsHideInTray)
            tray.Show();
        else
            tray.Dispose();

        using var registry = Registry.CurrentUser.OpenSubKey(AutorunKey, writable: true);
        if (settings.IsAutoRun)
            registry?.SetValue("Castar", $"\"{Environment.ProcessPath}\"");
        else
            registry?.DeleteValue("Castar", throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        try
        {
            serviceProvider?.Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
