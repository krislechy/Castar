using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Castar.Server;

internal sealed class LocalKeyAdmin(
    IConfiguration configuration, IServer server, ApiKeyStore keys, ILogger<LocalKeyAdmin> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private FileStream? directoryLease;
    private string? boundPath;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Castar local key administration requires a Unix socket.");

        var directory = Path.GetFullPath(configuration["Castar:LocalAdminDirectory"]!);
        const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (File.GetUnixFileMode(directory) != privateDirectory)
            throw new InvalidOperationException("Castar local key administration requires a private directory (0700).");

        var path = Path.Combine(directory, "admin.sock");
        directoryLease = File.Open(Path.Combine(directory, "admin.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        // The service owns this socket path. A forcible launchd stop can leave the socket on disk.
        File.Delete(path);
        listener.Bind(OperatingSystem.IsIOS() ? new IosSocketEndPoint(path) : new UnixDomainSocketEndPoint(path));
        boundPath = path;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(8);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptAsync(stoppingToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await using var stream = new NetworkStream(client);
                var request = await ReadCommandAsync(stream, deadline.Token);
                var response = await HandleCommandAsync(request, deadline.Token);
                await JsonSerializer.SerializeAsync(stream, response, response.GetType(), JsonOptions, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // A disconnected controller does not stop the listener.
            }
        }
    }

    private static async Task<string> ReadCommandAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[512];
        var used = 0;
        while (used < buffer.Length)
        {
            var received = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (received == 0)
                break;
            var newline = Array.IndexOf(buffer, (byte)'\n', used, received);
            if (newline >= 0)
                return Encoding.UTF8.GetString(buffer, 0, newline);
            used += received;
        }
        return "";
    }

    private async Task<object> HandleCommandAsync(string command, CancellationToken token)
    {
        var separator = command.IndexOf(' ');
        var verb = separator < 0 ? command : command[..separator];
        var argument = separator < 0 ? "" : command[(separator + 1)..];
        try
        {
            switch (verb)
            {
                case "list" when argument.Length == 0:
                    var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
                    return new
                    {
                        address = addresses is { Count: > 0 } ? string.Join(", ", addresses) : configuration["urls"] ?? "",
                        clientAddress = configuration["Castar:ClientAddress"] ?? "",
                        keys = keys.List()
                    };
                case "create":
                    return new { key = await keys.CreateAsync(argument, token) };
                case "copy" when Guid.TryParseExact(argument, "D", out var copyId):
                    return keys.CopyConfigured(copyId) is { } key
                        ? new { key }
                        : new { error = "Ключ недоступен для копирования." };
                case "revoke" when Guid.TryParseExact(argument, "D", out var revokeId):
                    return await keys.RevokeAsync(revokeId, token)
                        ? new { ok = true }
                        : new { error = "Ключ уже отозван или отсутствует." };
                default:
                    return new { error = "Неизвестная команда." };
            }
        }
        catch (ArgumentException)
        {
            return new { error = "Укажите имя ключа длиной до 80 символов без переносов строк." };
        }
        catch (ApiKeyLimitException)
        {
            return new { error = $"Достигнут предел в {ApiKeyStore.MaxActiveKeys} ключей. Отзовите один из действующих." };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "The local Castar administrator could not save the API key registry.");
            return new { error = "Не удалось сохранить ключи API." };
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        listener.Dispose();
        try
        {
            if (boundPath is not null)
                File.Delete(boundPath);
        }
        finally
        {
            directoryLease?.Dispose();
        }
    }

    // The iOS runtime's UnixDomainSocketEndPoint support probe returns false.
    // Darwin supports AF_UNIX and uses a length byte before the family and path.
    private sealed class IosSocketEndPoint(string path) : EndPoint
    {
        public override AddressFamily AddressFamily => AddressFamily.Unix;

        public override SocketAddress Serialize()
        {
            const int pathOffset = 2;
            const int pathCapacity = 104;
            var address = new SocketAddress(AddressFamily, pathOffset + pathCapacity);
            address[0] = (byte)address.Size;
            Encoding.UTF8.GetBytes(path, address.Buffer.Span.Slice(pathOffset, pathCapacity - 1));
            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
