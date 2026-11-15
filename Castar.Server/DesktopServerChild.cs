using System.Diagnostics;

namespace Castar.Server;

internal sealed class DesktopServerChild : IDisposable
{
    internal const string ControlVariable = "Castar__ServerUpdateControlDirectory";
    internal const string OwnerVariable = "Castar__ServerSupervisorOwner";
    private readonly string stopMarker;
    private readonly string owner;
    private readonly FileStream lease;
    private readonly CancellationTokenSource tracking = new();
    private Task watch = Task.CompletedTask;

    private DesktopServerChild(string directory, string owner, Guid instance)
    {
        this.owner = owner;
        stopMarker = StopMarker(directory, instance);
        lease = OpenLease(directory);
    }

    internal static DesktopServerChild? Open(IConfiguration configuration)
    {
        var owner = configuration["Castar:ServerSupervisorOwner"];
        if (owner is null)
            return null;

        var directory = configuration["Castar:ServerUpdateControlDirectory"];
        var fields = owner.Split(':');
        if (directory is null || !Path.IsPathFullyQualified(directory)
            || fields.Length != 3 || !Guid.TryParseExact(fields[2], "N", out var instance)
            || !SupervisorIsAlive(owner))
            throw new InvalidOperationException("Cannot identify the Castar server supervisor.");

        var child = new DesktopServerChild(directory, owner, instance);
        if (SupervisorIsAlive(owner))
            return child;
        child.Dispose();
        throw new InvalidOperationException("The Castar server supervisor has stopped.");
    }

    internal static FileStream OpenLease(string directory) =>
        new(Path.Combine(directory, "child.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal static string StopMarker(string directory, Guid instance) => Path.Combine(directory, $"stop-{instance:N}");

    internal static long StartMarker(Process process)
    {
        if (!OperatingSystem.IsLinux())
            return process.StartTime.ToUniversalTime().Ticks;

        var stat = File.ReadAllText(FormattableString.Invariant($"/proc/{process.Id}/stat"));
        var closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis >= 0)
        {
            // Linux reports starttime as field 22. The first field after comm is field 3.
            var fields = stat[(closingParenthesis + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 19 && long.TryParse(fields[19], out var ticks))
                return ticks;
        }

        throw new InvalidDataException("Cannot read the supervisor start time from procfs.");
    }

    internal void Watch(IHostApplicationLifetime application) => watch = WatchAsync(application);

    private async Task WatchAsync(IHostApplicationLifetime application)
    {
        try
        {
            while (!tracking.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), tracking.Token);
                if (File.Exists(stopMarker) || !SupervisorIsAlive(owner))
                {
                    application.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (tracking.IsCancellationRequested) { }
    }

    private static bool SupervisorIsAlive(string owner)
    {
        var fields = owner.Split(':');
        if (fields.Length != 3 || !int.TryParse(fields[0], out var id) || !long.TryParse(fields[1], out var ticks))
            return false;

        try
        {
            using var supervisor = Process.GetProcessById(id);
            return !supervisor.HasExited && StartMarker(supervisor) == ticks;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        tracking.Cancel();
        try
        {
            watch.GetAwaiter().GetResult();
        }
        finally
        {
            lease.Dispose();
            tracking.Dispose();
        }
    }
}
