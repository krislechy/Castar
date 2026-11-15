using System.Net;
using System.Text.Json;
using Castar.Api;
using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Castar.Server;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Castar.Services.TelegramBot.Commands;
using Microsoft.EntityFrameworkCore;

if (args is ["--stage-server-update", var updateWork, var updateStatus])
    return await ServerUpdateStaging.RunAsync(updateWork, updateStatus);
if (args is ["--supervise-server", var installationRoot])
    return await DesktopServerSupervisor.RunAsync(installationRoot);

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
builder.Services.AddCastarData(configuration);
builder.Services.AddSignalR();
builder.Services.AddSingleton<BudgetEventPublisher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BudgetEventPublisher>());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
builder.Services.AddSingleton<IReceiptProvider>(_ => new Nalog(() =>
{
    var credentials = settings.NalogFNS;
    return new NetworkCredential(credentials.INN, credentials.Password);
}));
builder.Services.AddSingleton<ServerSettings>();
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
