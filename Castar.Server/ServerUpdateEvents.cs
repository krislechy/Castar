using System.Threading.Channels;
using Castar.Api;
using Microsoft.AspNetCore.SignalR;

namespace Castar.Server;

internal sealed class ServerUpdateEvents(
    IConfiguration configuration,
    IHubContext<BudgetEventsHub> hub,
    ApiKeyStore keys,
    ILogger<ServerUpdateEvents> logger) : BackgroundService
{
    private readonly string? desktopDirectory = configuration["Castar:ServerUpdateControlDirectory"];
    private readonly SemaphoreSlim refresh = new(1);
    private ServerUpdateSnapshotDto snapshot = new(0, null);

    public static string GroupName(Guid keyId) => "updates:" + keyId.ToString("N");

    public Task<ServerUpdateSnapshotDto> GetSnapshotAsync(CancellationToken token) => RefreshAsync(token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = OperatingSystem.IsIOS() ? ServerUpdateApi.IosStatusDirectory : desktopDirectory;
        if (directory is null || !Directory.Exists(directory))
            return;

        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        using var tracking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var observer = UpdateStatusFileObserver.RunAsync(directory, () => changed.Writer.TryWrite(true), tracking.Token);
        var publisher = PublishChangesAsync();
        try
        {
            await Task.WhenAny(observer, publisher);
            tracking.Cancel();
            await Task.WhenAll(observer, publisher);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Could not watch Castar Server update status: {Error}", error.GetType().Name);
        }

        async Task PublishChangesAsync()
        {
            await foreach (var _ in changed.Reader.ReadAllAsync(tracking.Token))
                await RefreshAsync(tracking.Token);
        }
    }

    private async Task<ServerUpdateSnapshotDto> RefreshAsync(CancellationToken token)
    {
        ServerUpdateSnapshotDto next;
        await refresh.WaitAsync(token);
        try
        {
            var status = await ServerUpdateApi.ReadStatus(desktopDirectory, token);
            if (status is null || status == snapshot.Status)
                return snapshot;
            next = snapshot = new(snapshot.Sequence + 1, status);
        }
        finally
        {
            refresh.Release();
        }

        var groups = keys.GetActiveKeyIds().Select(GroupName).ToArray();
        if (groups.Length == 0)
            return next;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await hub.Clients.Groups(groups).SendAsync(ServerSynchronization.UpdateChanged, next, timeout.Token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Could not send Castar Server update status: {Error}", error.GetType().Name);
        }
        return next;
    }
}
