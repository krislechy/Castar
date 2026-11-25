using Castar.Contents;
using Castar.Currency;
using Castar.LocalAi;
using Castar.Notifications;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Castar;

public partial class App
{
    private ServiceProvider CreateClientServices(ClientSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cLogger);
        services.AddSingleton(settings);
        services.AddSingleton<CastarServerClient>();
        services.AddSingleton<HistoricalExchangeRates>();
        services.AddSingleton<BudgetEventConnection>();
        services.AddSingleton<LocalAiService>();
        services.AddSingleton<ExpenseCategorizationService>();
        services.AddSingleton<Updates.GitHubUpdateService>();
        services.AddSingleton<ITrayIcon>(_ => CreateTray("Castar", RestoreFromTray, RestoreFromTray, ExitFromTray));
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsContent>();
        services.AddSingleton<IncomesContent>();
        services.AddSingleton<PurchasesContent>();
        services.AddSingleton<CategoriesContent>();
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

        return services.BuildServiceProvider(validateScopes: true);
    }
}
