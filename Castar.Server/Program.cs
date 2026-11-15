using System.Net;
using System.Security.Cryptography;
using System.Text;
using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Castar.Server;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot;
using Castar.Services.TelegramBot.Commands;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
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
if (settings.Systems.IsEnableTelegramBot && string.IsNullOrWhiteSpace(settings.TelegramBot.TelegramBotApi_KEY))
{
    Console.Error.WriteLine("Set Castar__Telegram__Token when enabling the Telegram bot.");
    return 2;
}

if (string.IsNullOrWhiteSpace(configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddCastarData(configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
builder.Services.AddSingleton<IReceiptProvider>(_ => new Nalog(
    new NetworkCredential(settings.NalogFNS.INN, settings.NalogFNS.Password)));
builder.Services.AddScoped<CommandHandler>();
builder.Services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();

await using var app = builder.Build();
app.UseExceptionHandler();
var apiKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        var authorization = context.Request.Headers.Authorization;
        const string scheme = "Bearer ";
        var header = authorization.Count == 1 ? authorization[0] : null;
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(apiKeyHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..]))))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/status", async (CastarDbContext database, CancellationToken token) =>
{
    return await database.Database.CanConnectAsync(token)
        ? Results.Ok(new { apiVersion = 1 })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
app.MapCastarApi();
if (settings.Receipts.IsEnabled && !app.Services.GetServices<IReceiptProvider>()
    .Any(provider => string.Equals(provider.Id, settings.Receipts.Provider, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("Castar__Receipts__Provider does not match any registered receipt provider.");
    return 2;
}

await app.InitializeCastarDataAsync();
if (settings.Systems.IsEnableTelegramBot)
    app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<ICastarTelegramBotClient>());
await app.RunAsync();
return 0;
