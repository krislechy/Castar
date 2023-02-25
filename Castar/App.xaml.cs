using Castar.BL;
using Castar.BL.Settings;
using Castar.BL.Settings.Autorun;
using Castar.Compontents;
using Castar.Contents;
using Castar.DAL.Repositories.DataBase;
using Castar.DAL.Repositories.Settings;
using Castar.Logger;
using Castar.Security.Cryptography;
using Castar.Services.Receipts;
using Castar.Services.Receipts.Barcode;
using Castar.Services.Receipts.Models;
using Castar.Services.TelegramBot;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace Castar
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ICLogger cLogger;
        //@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=C:\DbCastar\castardb.mdf;Integrated Security=True;Connect Timeout=30"
        public App()
        {
            var services = new ServiceCollection();

            //Logging
            cLogger = new CLogger("Logs/logs.log");
            services.AddSingleton<ICLogger>((e) => cLogger);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;

            //Cryptography
            var cryptography = new Cryptography(GetUniqueMachineId(), GetExecutionPathToBinaryExe());
            services.AddScoped<ICryptography>((e) => cryptography);

            //DataBase Connection String & Settings repository
            var settingsRepository = new SettingsRepository(AppDomain.CurrentDomain.BaseDirectory + "Castar.Settings.xml", cryptography);
            var settings = settingsRepository.Load();
            string connectionString = @$"Data Source={settings.DataBase.DataSource};
                                        AttachDbFilename={settings.DataBase.AttachDBFileName};";
            if (!String.IsNullOrEmpty(settings.DataBase.Password) && !String.IsNullOrEmpty(settings.DataBase.Login))
                connectionString += $"User ID={settings.DataBase.Login};Password={settings.DataBase.Password};";
            else connectionString += "Integrated Security=True;";

            //Services DataBase
            services.AddScoped<IPurchasesService, PurchasesService>();
            services.AddScoped<IItemsService, ItemsService>();
            services.AddScoped<IIncomesService, IncomesService>();
            services.AddScoped<IUsersService, UsersService>();

            //Service Settings
            services.AddSingleton<ISettingsService, SettingsService>();

            //Settings helper
            services.AddScoped<ITrayIcon>((e) => GetTrayIconInstance());
            services.AddScoped<IAutorun>((e) => GetAutorunInstance());

            //Repositories DataBase
            services.AddScoped<IPurchasesRepository>((e) => new PurchasesRepository(connectionString));
            services.AddScoped<IItemsRepository>((e) => new ItemsRepository(connectionString));
            services.AddScoped<IIncomesRepository>((e) => new IncomesRepository(connectionString));
            services.AddScoped<IUsersRepository>((e) => new UsersRepository(connectionString));

            //Repository Settings
            services.AddSingleton<ISettingsRepository>((e) => settingsRepository);

            //TelegramBot
            services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();

            services.AddSingleton<MainWindow>();
            services.AddSingleton<SettingsContent>();
            services.AddSingleton<IncomesContent>();

            var serviceProvider = services.BuildServiceProvider();
            
            ResolveResourceDictionary();
            ResolveSettings(serviceProvider);
            ResolveTelegramBot(serviceProvider);
            InitializeCultures();

            var window = serviceProvider.GetService<MainWindow>();
            window!.Show();
        }
    }
}
