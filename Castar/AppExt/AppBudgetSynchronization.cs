using Castar.Api;
using Castar.Contents;
using Castar.Logger;
using Castar.Notifications;
using Castar.ServerConnection;
using Castar.Settings;
using Castar.Statistics;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Castar;

public partial class App
{
    private BudgetEventConnection AttachBudgetSynchronization(ServiceProvider services, ClientSettings settings,
        ITrayIcon tray, CastarNotificationCenter notifications)
    {
        var budgetEvents = services.GetRequiredService<BudgetEventConnection>();
        var purchases = services.GetRequiredService<PurchasesContent>();
        var incomes = services.GetRequiredService<IncomesContent>();
        var categories = services.GetRequiredService<CategoriesContent>();
        var statistics = services.GetRequiredService<Lazy<StatisticsContent>>();
        var budgetStatistics = services.GetRequiredService<BudgetStatistics>();
        var server = services.GetRequiredService<CastarServerClient>();
        var settingsView = services.GetRequiredService<SettingsContent>();
        var synchronizedOnce = false;
        var chartReloadDeferred = false;
        settings.ConnectionChanged += (_, _) => synchronizedOnce = false;
        settingsView.ClearChartCacheAsync = async () =>
        {
            await budgetStatistics.ClearChartCacheAsync();
            if (statistics.IsValueCreated)
            {
                chartReloadDeferred = settingsView.IsVisible;
                _ = statistics.Value.RefreshAfterCacheClearAsync(reload: !chartReloadDeferred);
            }
        };
        settingsView.ScreenClosed += () =>
        {
            if (!chartReloadDeferred)
                return;
            chartReloadDeferred = false;
            _ = Dispatcher.InvokeAsync(() => statistics.Value.RefreshAfterCacheClearAsync(), DispatcherPriority.Background);
        };

        async Task RefreshFinancialStatisticsAsync(DateOnly? affectedDay)
        {
            try
            {
                await budgetStatistics.InvalidateFinancialCacheAsync(affectedDay);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                cLogger.Log<App>("Не удалось обновить файл кеша графиков.", TypeLog.Warning);
            }
            if (statistics.IsValueCreated && !chartReloadDeferred)
                _ = statistics.Value.RefreshAfterServerChangeAsync();
        }

        DateOnly? AffectedDay(DateTimeOffset? instant) => server.DisplayOperationDate(null, instant) is { } date
            ? DateOnly.FromDateTime(date) : null;

        server.FinancialMutationAsync = instant => Dispatcher.InvokeAsync(
            () => RefreshFinancialStatisticsAsync(AffectedDay(instant)), DispatcherPriority.Background).Task.Unwrap();

        void RefreshOpenCategoryChart()
        {
            if (statistics.IsValueCreated)
                statistics.Value.RefreshCategoryChartAfterServerChange();
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
                        AddedOn = services.GetRequiredService<CastarServerClient>()
                            .DisplayOperationDate(change.AddedOn, change.AddedOnUtc)
                    });
                else
                    tray.Notify(BudgetNotificationText.Format(change.Kind, Math.Max(1, change.CreatedCount), change.Preview));
            }
            _ = RefreshFinancialStatisticsAsync(AffectedDay(change.AffectedFromUtc));
        };
        budgetEvents.Synchronized += () =>
        {
            services.GetRequiredService<CastarServerClient>().ClearAvatarCache();
            incomes.InvalidateCategoryNames();
            purchases.ResumePurchaseItemQueue();
            purchases.RefreshRecurringPurchasesAfterServerChange();
            _ = purchases.RefreshAfterServerChangeAsync();
            _ = incomes.RefreshAfterServerChangeAsync();
            _ = categories.RefreshAfterServerChangeAsync();
            if (synchronizedOnce)
                _ = RefreshFinancialStatisticsAsync(null);
            synchronizedOnce = true;
        };
        budgetEvents.ConfigurationChanged += change =>
        {
            if (change.Kind == ServerSynchronization.RecurringPurchases)
            {
                purchases.RefreshRecurringPurchasesAfterServerChange();
                return;
            }
            if (change.Kind == ServerSynchronization.PurchaseClassification)
            {
                _ = purchases.RefreshAfterServerChangeAsync();
                RefreshOpenCategoryChart();
                return;
            }
            if (change.Kind == ServerSynchronization.IncomeClassification)
            {
                _ = incomes.RefreshAfterServerChangeAsync();
                RefreshOpenCategoryChart();
                return;
            }
            if (change.Kind == ServerSynchronization.Categories)
            {
                _ = categories.RefreshAfterServerChangeAsync();
                purchases.RefreshRecurringPurchasesAfterServerChange();
                _ = purchases.RefreshAfterServerChangeAsync();
                incomes.InvalidateCategoryNames();
                _ = incomes.RefreshAfterServerChangeAsync();
                RefreshOpenCategoryChart();
                return;
            }
            if (change.Kind == ServerSynchronization.Users)
            {
                services.GetRequiredService<CastarServerClient>().ClearAvatarCache();
                purchases.RefreshRecurringPurchasesAfterServerChange();
                _ = purchases.RefreshAfterServerChangeAsync();
                _ = incomes.RefreshAfterServerChangeAsync();
            }
        };

        return budgetEvents;
    }
}
