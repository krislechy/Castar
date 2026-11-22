using System.Net;
using System.IO.Compression;
using System.Text.Json;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Castar.Server;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Castar.Services.TelegramBot.Commands;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

if (args is ["--stage-server-update", var updateWork, var updateStatus])
    return await ServerUpdateStaging.RunAsync(updateWork, updateStatus);
if (args is ["--supervise-server", var installationRoot])
    return await DesktopServerSupervisor.RunAsync(installationRoot);
if (args is ["--import-legacy", .. var importArguments])
    return await LegacyImport.RunAsync(importArguments);

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
using var supervisedChild = DesktopServerChild.Open(configuration);
var verifyingUpdate = configuration.GetValue<bool>("Castar:UpdateVerification");
var activationGate = configuration["Castar:UpdateActivationGate"];
if (activationGate is not null && !Path.IsPathFullyQualified(activationGate))
    throw new InvalidOperationException("UpdateActivationGate must be absolute.");
bool IsUpdating() => verifyingUpdate || (activationGate is not null && File.Exists(activationGate));
var apiKey = configuration["Castar:ApiKey"]?.Trim();
if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 32 || apiKey.Any(char.IsWhiteSpace))
{
    Console.Error.WriteLine("Set Castar__ApiKey to a random key of at least 32 characters without spaces.");
    return 2;
}

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
if (string.IsNullOrWhiteSpace(configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ["application/json"];
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.AddCastarData(configuration);
builder.Services.AddExchangeRates();
builder.Services.AddSignalR();
builder.Services.AddSingleton<BudgetEventPublisher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BudgetEventPublisher>());
builder.Services.AddSingleton<ServerUpdateEvents>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerUpdateEvents>());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
builder.Services.AddSingleton<IReceiptProvider>(_ => new Nalog(() =>
{
    var credentials = settings.NalogFNS;
    return new NetworkCredential(credentials.INN, credentials.Password);
}));
builder.Services.AddSingleton<ServerSettings>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => new RecurringPurchaseScheduler(
    provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<BudgetEventPublisher>(),
    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RecurringPurchaseScheduler>>(), IsUpdating));
if (!verifyingUpdate)
    builder.Services.AddHostedService(provider => provider.GetRequiredService<RecurringPurchaseScheduler>());
builder.Services.AddSingleton(provider => new ReceiptQueueNotifications(settings,
    provider.GetRequiredService<ILogger<ReceiptQueueNotifications>>(),
    new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
builder.Services.AddSingleton(provider => new ReceiptImportQueue(
    provider.GetRequiredService<IServiceScopeFactory>(), settings, provider.GetRequiredService<ServerSettings>(),
    provider.GetServices<IReceiptProvider>(), provider.GetRequiredService<ReceiptQueueNotifications>(),
    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ReceiptImportQueue>>(), IsUpdating));
builder.Services.AddSingleton<IReceiptImportQueue>(provider => provider.GetRequiredService<ReceiptImportQueue>());
if (!verifyingUpdate)
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ReceiptImportQueue>());
builder.Services.AddSingleton<ApiKeyStore>();
if (!verifyingUpdate && !string.IsNullOrWhiteSpace(configuration["Castar:LocalAdminDirectory"]))
    builder.Services.AddHostedService<LocalKeyAdmin>();
builder.Services.AddScoped<CommandHandler>();
builder.Services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();

await using var app = builder.Build();
supervisedChild?.Watch(app.Lifetime);
app.UseExceptionHandler();
var keys = app.Services.GetRequiredService<ApiKeyStore>();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        if (IsUpdating())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var authorization = context.Request.Headers.Authorization;
        const string scheme = "Bearer ";
        var header = authorization.Count == 1 ? authorization[0] : null;
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || keys.GetAuthorizedKeyId(header[scheme.Length..]) is not Guid keyId)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        context.Items[typeof(BudgetEventsHub)] = keyId;
    }
    await next(context);
});
app.UseWhen(context => HttpMethods.IsGet(context.Request.Method) &&
    (context.Request.Path.Equals("/api/purchases/summary", StringComparison.OrdinalIgnoreCase) ||
     context.Request.Path.Equals("/api/budget/operations", StringComparison.OrdinalIgnoreCase)),
    branch => branch.UseResponseCompression());

app.MapGet("/health", () => Results.Ok(new
{
    status = "ready", version = ServerUpdateStaging.InstalledVersion.ToString(), processId = Environment.ProcessId
}));
app.MapGet("/api/status", async (CastarDbContext database, CancellationToken token) =>
{
    return await database.Database.CanConnectAsync(token)
        ? Results.Ok(new { apiVersion = 1 })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
app.MapCastarApi();
app.MapServerUpdate();
app.MapHub<BudgetEventsHub>("/" + BudgetEvents.Path);
try
{
    await app.Services.GetRequiredService<ServerSettings>().InitializeAsync();
}
catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot load Castar Server settings ({error.GetType().Name}).");
    return 2;
}

await app.InitializeCastarDataAsync();
try
{
    await keys.InitializeAsync();
}
catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot load Castar API keys. {error.Message}");
    return 2;
}
if (!verifyingUpdate)
    app.Lifetime.ApplicationStarted.Register(() => _ = StartTelegramAsync());
await app.RunAsync();
return 0;

async Task StartTelegramAsync()
{
    var stopping = app.Lifetime.ApplicationStopping;
    try
    {
        while (IsUpdating())
            await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
        if (!stopping.IsCancellationRequested)
            app.Services.GetRequiredService<ICastarTelegramBotClient>();
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
    }
}
