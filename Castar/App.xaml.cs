using Castar.BL;
using Castar.BL.Settings;
using Castar.BL.Settings.Autorun;
using Castar.Contents;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Castar.DAL.Repositories.Settings;
using Castar.Logger;
using Castar.Security.Cryptography;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Castar.Services.TelegramBot.Commands;
using Castar.TrayNotify.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Net;
using System.Windows;

namespace Castar
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly ICLogger cLogger = new CLogger(Path.Combine(DatabaseConnections.DataDirectory, "Logs", "castar.log"));
        private ServiceProvider? serviceProvider;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            InitializeCultures();
            var services = new ServiceCollection();

            services.AddSingleton(cLogger);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            var cryptography = new Cryptography(GetUniqueMachineId(), GetExecutionPathToBinaryExe());
            var legacySettingsPath = Path.Combine(AppContext.BaseDirectory, "Castar.Settings.xml");
            var settingsPath = File.Exists(legacySettingsPath)
                ? legacySettingsPath
                : Path.Combine(DatabaseConnections.DataDirectory, "Castar.Settings.xml");
            var settingsRepository = new SettingsRepository(settingsPath, cryptography);
            var settingsService = new SettingsService(settingsRepository);
            var database = settingsService.Get().Copy().DataBase;
            var useSqlite = database.Engine == Domain.Models.SettingsModel.DatabaseEngine.Sqlite;

            services.AddDbContextFactory<CastarDbContext>(options =>
            {
                if (useSqlite)
                    options.UseSqlite(DatabaseConnections.GetSqliteConnectionString(database));
                else
                    options.UseSqlServer(DatabaseConnections.GetSqlServerConnectionString(database),
                        sql => sql.UseCompatibilityLevel(130).EnableRetryOnFailure());
            });
            services.AddSingleton<IPurchasesRepository, PurchasesRepository>();
            services.AddSingleton<IItemsRepository, ItemsRepository>();
            services.AddSingleton<IIncomesRepository, IncomesRepository>();
            services.AddSingleton<IUsersRepository, UsersRepository>();
            services.AddSingleton<IPurchasesService, PurchasesService>();
            services.AddSingleton<IItemsService, ItemsService>();
            services.AddSingleton<IIncomesService, IncomesService>();
            services.AddSingleton<IUsersService, UsersService>();

            services.AddSingleton<ISettingsService>(settingsService);
            services.AddSingleton<ITrayIcon>(_ => GetTrayIconInstance());
            services.AddSingleton<IAutorun>(_ => GetAutorunInstance());
            services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
            services.AddSingleton<IReceiptProvider>(_ => new Nalog(() =>
            {
                var nalog = settingsService.Get().NalogFNS;
                return new NetworkCredential(nalog.INN, nalog.Password);
            }));
            services.AddScoped<CommandHandler>();
            services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();
            services.AddSingleton<Updates.GitHubUpdateService>();

            services.AddSingleton<MainWindow>();
            services.AddSingleton<SettingsContent>();
            services.AddSingleton<IncomesContent>();

            serviceProvider = services.BuildServiceProvider(validateScopes: true);
            if (useSqlite)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DatabaseConnections.GetSqlitePath(database))!);
                    var factory = serviceProvider.GetRequiredService<IDbContextFactory<CastarDbContext>>();
                    await using var context = await factory.CreateDbContextAsync();
                    await context.Database.EnsureCreatedAsync();
                }
                catch (Exception error) when (error is System.Data.Common.DbException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    MessageBox.Show("Не удалось открыть SQLite. Проверьте путь к базе данных в настройках.",
                        "Castar", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            ResolveSettings(serviceProvider);
            serviceProvider.GetRequiredService<ICastarTelegramBotClient>();
            MainWindow = serviceProvider.GetRequiredService<MainWindow>();
            MainWindow.Show();
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
}
