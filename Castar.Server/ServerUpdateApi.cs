using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Castar.Api;

namespace Castar.Server;

internal static class ServerUpdateApi
{
    private const string ControlSocket = "/var/jb/var/run/castar/control.sock";
    internal const string IosStatusDirectory = "/var/jb/var/run/castar";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void MapServerUpdate(this WebApplication app)
    {
        var desktopControl = app.Configuration["Castar:ServerUpdateControlDirectory"];
        if (desktopControl is not null && !Path.IsPathFullyQualified(desktopControl))
            throw new InvalidOperationException("ServerUpdateControlDirectory must be absolute.");

        app.MapGet("/api/server-update", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ReadStatus(desktopControl, context.RequestAborted) is { } status
                ? Results.Ok(status) : Results.NotFound();
        });
        app.MapPost("/api/server-update", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await ReadStatus(desktopControl, context.RequestAborted) is not { } status)
                return Results.NotFound();
            if (status.State is "checking" or "downloading" or "installing")
                return Results.Accepted(value: status);
            var accepted = OperatingSystem.IsIOS()
                ? await RunCommand("update\n", context.RequestAborted) == "ok\n"
                : RequestDesktopUpdate(desktopControl);
            if (!accepted)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Accepted(value: new ServerUpdateStatusDto("checking", ServerUpdateStaging.InstalledVersion.ToString()));
        });
    }

    internal static async Task<ServerUpdateStatusDto?> ReadStatus(string? desktopControl, CancellationToken token)
    {
        var reply = OperatingSystem.IsIOS() ? await RunCommand("update-status\n", token) :
            ReadDesktopStatus(desktopControl);
        if (reply is null || !reply.StartsWith('{'))
            return null;
        try
        {
            var status = JsonSerializer.Deserialize<ServerUpdateStatusDto>(reply, JsonOptions);
            return status is not null && string.IsNullOrEmpty(status.InstalledVersion)
                ? status with { InstalledVersion = ServerUpdateStaging.InstalledVersion.ToString() }
                : status;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadDesktopStatus(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
            return null;
        try
        {
            using var file = new FileStream(Path.Combine(directory, "update.json"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 4096)
                return null;
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return "{\"state\":\"idle\"}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool RequestDesktopUpdate(string? directory)
    {
        if (directory is null)
            return false;
        var request = Path.Combine(directory, "request");
        try
        {
            using var file = new FileStream(request, FileMode.CreateNew, FileAccess.Write);
            return true;
        }
        catch (IOException)
        {
            return File.Exists(request);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string?> RunCommand(string command, CancellationToken requestAborted)
    {
        if (!OperatingSystem.IsIOS())
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new LocalKeyAdmin.IosSocketEndPoint(ControlSocket), timeout.Token);
            await using var stream = new NetworkStream(socket);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            socket.Shutdown(SocketShutdown.Send);
            var buffer = new byte[4096];
            int used = 0;
            while (used < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(used), timeout.Token);
                if (count == 0)
                    return Encoding.UTF8.GetString(buffer, 0, used);
                used += count;
            }
            return null;
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
