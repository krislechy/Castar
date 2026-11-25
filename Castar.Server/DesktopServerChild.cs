using System.Diagnostics;
using System.Globalization;

namespace Castar.Server;

internal sealed class DesktopServerChild : IDisposable
{
    internal const string ControlVariable = "Castar__ServerUpdateControlDirectory";
    internal const string OwnerVariable = "Castar__ServerSupervisorOwner";
    private readonly string stopMarker;
    private static readonly TimeSpan OwnerCheckInterval = TimeSpan.FromSeconds(2);
    private readonly int supervisorId;
    private readonly long supervisorStart;
    private readonly FileStream lease;
    private readonly CancellationTokenSource tracking = new();
    private Task watch = Task.CompletedTask;

    private DesktopServerChild(string directory, int supervisorId, long supervisorStart, Guid instance)
    {
        this.supervisorId = supervisorId;
        this.supervisorStart = supervisorStart;
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
            || fields.Length != 3
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !Guid.TryParseExact(fields[2], "N", out var instance) || !SupervisorIsAlive(id, ticks))
            throw new InvalidOperationException("Cannot identify the Castar server supervisor.");

        var child = new DesktopServerChild(directory, id, ticks, instance);
        if (SupervisorIsAlive(id, ticks))
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

        return ReadLinuxProcess(process.Id).Start;
    }

    private static (long Start, bool Exited) ReadLinuxProcess(int id)
    {
        var stat = File.ReadAllText(FormattableString.Invariant($"/proc/{id}/stat"));
        var closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis >= 0)
        {
            // Linux reports starttime as field 22. The first field after comm is field 3.
            var fields = stat[(closingParenthesis + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 19 && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return (ticks, fields[0] is "Z" or "X" or "x");
        }

        throw new InvalidDataException("Cannot read the supervisor start time from procfs.");
    }

    internal void Watch(IHostApplicationLifetime application) => watch = WatchAsync(application);

    private async Task WatchAsync(IHostApplicationLifetime application)
    {
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = WatchStopMarker(stopRequested);
        using var supervisor = WatchSupervisorExit(stopRequested);
        while (!tracking.IsCancellationRequested && !stopRequested.Task.IsCompleted
            && !File.Exists(stopMarker) && (supervisor is not null || SupervisorIsAlive(supervisorId, supervisorStart)))
        {
            await Task.WhenAny(stopRequested.Task, Task.Delay(OwnerCheckInterval, tracking.Token));
        }
        if (!tracking.IsCancellationRequested)
            application.StopApplication();
    }

    private Process? WatchSupervisorExit(TaskCompletionSource requested)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        Process? supervisor = null;
        try
        {
            supervisor = Process.GetProcessById(supervisorId);
            // Retain the original handle so PID reuse cannot redirect the exit subscription.
            _ = supervisor.SafeHandle;
            if (supervisor.HasExited || StartMarker(supervisor) != supervisorStart)
            {
                requested.TrySetResult();
                return null;
            }
            supervisor.Exited += (_, _) => requested.TrySetResult();
            supervisor.EnableRaisingEvents = true;
            if (supervisor.HasExited)
                requested.TrySetResult();

            var observed = supervisor;
            supervisor = null;
            return observed;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // The portable owner check still runs if Windows cannot supply a waitable handle.
            return null;
        }
        finally
        {
            supervisor?.Dispose();
        }
    }

    private FileSystemWatcher? WatchStopMarker(TaskCompletionSource requested)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(stopMarker)!, Path.GetFileName(stopMarker))
            {
                NotifyFilter = NotifyFilters.FileName
            };
            watcher.Created += (_, _) => requested.TrySetResult();
            watcher.Renamed += (_, change) =>
            {
                if (change.FullPath.Equals(stopMarker, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    requested.TrySetResult();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            // The periodic owner and marker check also works without file notifications.
            return null;
        }
    }

    private static bool SupervisorIsAlive(int id, long ticks)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var (start, exited) = ReadLinuxProcess(id);
                return !exited && start == ticks;
            }
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
