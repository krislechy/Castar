using System.Net;
using System.Text.Json;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Castar.Services.TelegramBot.Commands;

namespace Castar.Server;

internal static class ServerHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var version = typeof(ServerHost).Assembly.GetName().Version!.ToString(3);
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;
        using var supervisedChild = DesktopServerChild.Open(configuration);
        var activation = new ServerActivationState(configuration);
        var apiKey = configuration["Castar:ApiKey"]?.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 32 || apiKey.Any(char.IsWhiteSpace))
        {
            Console.Error.WriteLine("Set Castar__ApiKey to a random key of at least 32 characters without spaces.");
            return 2;
        }

        CastarHttpPipeline.Configure(builder);
        ConfigureServices(builder.Services, configuration, activation);

        await using var app = builder.Build();
        supervisedChild?.Watch(app.Lifetime);
        var keys = app.Services.GetRequiredService<ApiKeyStore>();
        CastarHttpPipeline.Map(app, version, activation, keys);

        try
        {
            await app.Services.GetRequiredService<ServerSettings>().InitializeAsync();
        }
        catch (Exception error) when (IsStateReadError(error))
        {
            Console.Error.WriteLine($"Cannot load Castar Server settings ({error.GetType().Name}).");
            return 2;
        }

        await app.InitializeCastarDataAsync();
        try
        {
            await keys.InitializeAsync();
        }
        catch (Exception error) when (IsStateReadError(error))
        {
            Console.Error.WriteLine($"Cannot load Castar API keys ({error.GetType().Name}).");
            return 2;
        }

        if (!activation.VerifyingUpdate)
            app.Lifetime.ApplicationStarted.Register(() => _ = StartTelegramAsync(app, activation));
        await app.RunAsync();
        return 0;
    }

    private static bool IsStateReadError(Exception error) =>
        error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException;

    private static async Task StartTelegramAsync(WebApplication app, ServerActivationState activation)
    {
        var stopping = app.Lifetime.ApplicationStopping;
        try
        {
            await activation.WaitUntilReadyAsync(stopping);
            app.Services.GetRequiredService<ICastarTelegramBotClient>();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // Startup failures must be observable without taking down the financial API or exposing a token.
            app.Logger.LogError("Could not initialize the Telegram service ({ErrorType}).", error.GetType().Name);
        }
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration, ServerActivationState activation)
    {
        var settings = new SettingsModel
        {
            Systems = new Systems { IsEnableTelegramBot = configuration.GetValue<bool>("Castar:Telegram:Enabled") },
            TelegramBot = new TelegramBot { TelegramBotApi_KEY = configuration["Castar:Telegram:Token"] },
            Receipts = new ReceiptSettings
            {
                Provider = configuration["Castar:Receipts:Provider"] ?? SerbiaReceiptProvider.ProviderId,
                IsEnabled = configuration.GetValue("Castar:Receipts:Enabled", true)
            },
            NalogFNS = new NalogFNS
            {
                INN = configuration["Castar:Receipts:Nalog:Inn"],
                Password = configuration["Castar:Receipts:Nalog:Password"]
            }
        };

        services.AddCastarData(configuration);
        services.AddExchangeRates();
        services.AddSignalR();
        services.AddHttpContextAccessor();
        services.AddSingleton<BudgetEventPublisher>();
        services.AddHostedService(provider => provider.GetRequiredService<BudgetEventPublisher>());
        services.AddSingleton(settings);
        services.AddSingleton(activation);
        services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
        services.AddSingleton<IReceiptProvider>(_ => new Nalog(() =>
        {
            var credentials = settings.NalogFNS;
            return new NetworkCredential(credentials.INN, credentials.Password);
        }));
        services.AddSingleton<ServerSettings>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new RecurringPurchaseScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<BudgetEventPublisher>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RecurringPurchaseScheduler>>(),
            activation.IsBlocked));
        services.AddSingleton(provider => new ReceiptQueueNotifications(settings,
            provider.GetRequiredService<ILogger<ReceiptQueueNotifications>>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddSingleton(provider => new ReceiptImportQueue(
            provider.GetRequiredService<IServiceScopeFactory>(), settings, provider.GetRequiredService<ServerSettings>(),
            provider.GetServices<IReceiptProvider>(), provider.GetRequiredService<ReceiptQueueNotifications>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ReceiptImportQueue>>(),
            activation.IsBlocked));
        services.AddSingleton<IReceiptImportQueue>(provider => provider.GetRequiredService<ReceiptImportQueue>());

        if (!activation.VerifyingUpdate)
        {
            services.AddHostedService(provider => provider.GetRequiredService<RecurringPurchaseScheduler>());
            services.AddHostedService(provider => provider.GetRequiredService<ReceiptImportQueue>());
            if (!string.IsNullOrWhiteSpace(configuration["Castar:LocalAdminDirectory"]))
                services.AddHostedService<LocalKeyAdmin>();
        }

        services.AddSingleton<ApiKeyStore>();
        services.AddScoped<CommandHandler>();
        services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();
    }
}
