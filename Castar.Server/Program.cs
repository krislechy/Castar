using System.Net;
using System.Text.Json;
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
if (string.IsNullOrWhiteSpace(configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddCastarData(configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IReceiptProvider, SerbiaReceiptProvider>();
builder.Services.AddSingleton<IReceiptProvider>(_ => new Nalog(
    new NetworkCredential(settings.NalogFNS.INN, settings.NalogFNS.Password)));
builder.Services.AddSingleton<ServerSettings>();
builder.Services.AddSingleton<ApiKeyStore>();
if (!string.IsNullOrWhiteSpace(configuration["Castar:LocalAdminDirectory"]))
    builder.Services.AddHostedService<LocalKeyAdmin>();
builder.Services.AddScoped<CommandHandler>();
builder.Services.AddSingleton<ICastarTelegramBotClient, CastarTelegramBotClient>();

await using var app = builder.Build();
app.UseExceptionHandler();
var keys = app.Services.GetRequiredService<ApiKeyStore>();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        var authorization = context.Request.Headers.Authorization;
        const string scheme = "Bearer ";
        var header = authorization.Count == 1 ? authorization[0] : null;
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || !keys.IsAuthorized(header[scheme.Length..]))
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
try
{
    await app.Services.GetRequiredService<ServerSettings>().InitializeAsync();
}
catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot load Castar Server settings. {error.Message}");
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
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<ICastarTelegramBotClient>());
await app.RunAsync();
return 0;
