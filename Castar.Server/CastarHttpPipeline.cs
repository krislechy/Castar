using System.IO.Compression;
using Castar.Api;
using Castar.DAL;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal static class CastarHttpPipeline
{
    private const string Bearer = "Bearer ";

    internal static void Configure(WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
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
    }

    internal static void Map(WebApplication app, string version, ServerActivationState activation, ApiKeyStore keys)
    {
        app.UseExceptionHandler();
        app.Use((context, next) => AuthorizeAsync(context, next, activation, keys));
        app.UseWhen(context => HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path.Equals("/api/purchases/summary", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.Equals("/api/budget/operations", StringComparison.OrdinalIgnoreCase)),
            branch => branch.UseResponseCompression());

        app.MapGet("/health", () => Results.Ok(new { status = "ready", version, processId = Environment.ProcessId }));
        app.MapGet("/api/status", async (CastarDbContext database, CancellationToken token) =>
            await database.Database.CanConnectAsync(token) ? Results.Ok(new { apiVersion = 1 })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapCastarApi();
        app.MapHub<BudgetEventsHub>("/" + BudgetEvents.Path);
    }

    private static Task AuthorizeAsync(HttpContext context, RequestDelegate next, ServerActivationState activation, ApiKeyStore keys)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return next(context);

        if (activation.IsBlocked())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        var authorization = context.Request.Headers.Authorization;
        var header = authorization.Count == 1 ? authorization[0] : null;
        if (header is null || !header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase)
            || keys.GetAuthorizedKeyId(header.AsSpan(Bearer.Length)) is not Guid keyId)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }

        context.Items[typeof(BudgetEventsHub)] = keyId;
        return next(context);
    }
}
