using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Castar.Server;

internal sealed class DesktopServerSupervisor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(250);
    private readonly string current;
    private readonly string control;
    private readonly string journalPath;
    private readonly string databasePath;
    private readonly Uri health;
    private readonly HttpClient client;
    private readonly string owner;
    private Child? child;

    private DesktopServerSupervisor(string root, HttpClient client)
    {
        root = ResolvePath(Path.GetFullPath(root));
        if (!Directory.Exists(root))
            throw new IOException("The Castar Server installation directory does not exist.");
        current = Path.Combine(root, "current");
        control = Path.Combine(root, "control");
        journalPath = Path.Combine(control, "transaction");
        this.client = client;

        var database = Environment.GetEnvironmentVariable("Database__SqlitePath");
        if (database is null || !Path.IsPathFullyQualified(database))
            throw new InvalidOperationException("Set Database__SqlitePath to an absolute path outside current and control.");

        databasePath = ResolvePath(database);
        if (Inside(databasePath, current) || Inside(databasePath, control))
            throw new InvalidOperationException("SQLite must be outside current and control.");

        var bootstrap = ResolvePath(AppContext.BaseDirectory);
        if (Inside(bootstrap, current) || Inside(bootstrap, control))
            throw new InvalidOperationException("Run the supervisor from a separate bootstrap directory.");

        RejectLink(current);
        RejectLink(control);
        health = ReadHealthUri();
        using var self = Process.GetCurrentProcess();
        owner = FormattableString.Invariant($"{self.Id}:{DesktopServerChild.StartMarker(self)}");
    }

    internal static async Task<int> RunAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Castar desktop supervisor requires Windows or Linux.");
            return 2;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var term = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        }) : null;
        void Cancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            stopping.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = ConnectHealthAsync
            })
            {
                Timeout = TimeSpan.FromSeconds(2),
                MaxResponseContentBufferSize = 4096
            };
            var supervisor = new DesktopServerSupervisor(root, client);
            await supervisor.SuperviseAsync(stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or BadImageFormatException or JsonException)
        {
            Console.Error.WriteLine($"Castar server supervisor: {error.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }

    private async Task SuperviseAsync(CancellationToken token)
    {
        CreateControlDirectory();
        await using var supervisorLease = new FileStream(Path.Combine(control, "supervisor.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await WaitForPreviousChildAsync(token);
        if (File.Exists(journalPath))
            throw new IOException("Recover the interrupted update with the previous supervisor before replacing the server.");
        var version = ReadVersion(current);

        try
        {
            await StartAsync(version, token);
            while (true)
            {
                await Task.Delay(PollDelay, token);
                if (child!.Process.HasExited)
                {
                    await StopAsync();
                    Console.Error.WriteLine("Castar Server exited. Restarting the supervised process.");
                    await StartAsync(version, token);
                }
            }
        }
        finally
        {
            await StopAsync();
        }
    }

    private async Task WaitForPreviousChildAsync(CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var lease = DesktopServerChild.OpenLease(control);
                return;
            }
            catch (IOException) when (attempt < 80)
            {
                await Task.Delay(PollDelay, token);
            }
        }
    }

    private async Task StartAsync(Version version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var instance = Guid.NewGuid();
        var executable = Path.Combine(current, OperatingSystem.IsWindows() ? "Castar.Server.exe" : "Castar.Server");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = current };
        info.Environment[DesktopServerChild.ControlVariable] = control;
        info.Environment[DesktopServerChild.OwnerVariable] = $"{owner}:{instance:N}";
        info.Environment["Database__SqlitePath"] = databasePath;
        child = new(Process.Start(info) ?? throw new InvalidOperationException("Castar Server could not be started."),
            DesktopServerChild.StopMarker(control, instance));
        await WaitUntilHealthyAsync(child.Process, health, version, token);
    }

    private async Task WaitUntilHealthyAsync(Process process, Uri target, Version expected, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            while (true)
            {
                if (process.HasExited)
                    throw new IOException($"Castar Server exited before becoming healthy (code {process.ExitCode}).");
                try
                {
                    using var response = await client.GetAsync(target, deadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var healthReport = JsonSerializer.Deserialize<HealthReport>(
                            await response.Content.ReadAsByteArrayAsync(deadline.Token), JsonOptions);
                        if (healthReport?.Status == "ready" && healthReport.Version == expected.ToString()
                            && healthReport.ProcessId == process.Id)
                        {
                            await Task.Delay(PollDelay, deadline.Token);
                            if (!process.HasExited)
                                return;
                        }
                    }
                }
                catch (Exception error) when (error is HttpRequestException or JsonException
                    || error is OperationCanceledException && !deadline.IsCancellationRequested)
                {
                }
                await Task.Delay(PollDelay, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException($"Castar Server did not report healthy version {expected}.");
        }
    }

    private async Task StopAsync()
    {
        var active = child;
        if (active is null)
            return;
        if (!active.Process.HasExited)
        {
            try
            {
                using (File.Create(active.StopMarker)) { }
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await active.Process.WaitForExitAsync(graceful.Token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (!active.Process.HasExited)
                {
                    try { active.Process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (active.Process.HasExited) { }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await active.Process.WaitForExitAsync(deadline.Token); }
                    catch (OperationCanceledException timeout) when (deadline.IsCancellationRequested)
                    {
                        throw new IOException("Castar Server did not stop after termination.", timeout);
                    }
                }
            }
        }

        child = null;
        active.Process.Dispose();
        try { File.Delete(active.StopMarker); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Version ReadVersion(string directory)
    {
        var version = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(directory, "Castar.Server.dll")).Version
            ?? throw new IOException("Invalid Castar.Server.dll version.");
        return new Version(version.Major, version.Minor, version.Build);
    }

    private static Uri ReadHealthUri()
    {
        var value = Environment.GetEnvironmentVariable("Castar__ServerUpdateHealthUrl") ?? "http://127.0.0.1:5080/health";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !TryHealthAddresses(uri.Host, out _)
            || uri.AbsolutePath != "/health" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Castar__ServerUpdateHealthUrl must be an HTTP(S) /health URL for localhost or a loopback IP.");
        return uri;
    }

    private static bool TryHealthAddresses(string host, out IPAddress[] addresses)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            addresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))
            addresses = [ip];
        else
            addresses = [];
        return addresses.Length != 0;
    }

    private static async ValueTask<Stream> ConnectHealthAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!TryHealthAddresses(context.DnsEndPoint.Host, out var addresses))
            throw new HttpRequestException("The Castar Server health target is not local.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Inside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void CreateControlDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Cannot identify the Windows supervisor account.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
            if (!Directory.Exists(control))
                new DirectoryInfo(control).Create(security);
            else
                new DirectoryInfo(control).SetAccessControl(security);
            return;
        }

        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(control, mode);
        if (File.GetUnixFileMode(control) != mode)
            throw new IOException("The Castar Server control directory must have private permissions (0700).");
    }

    private static string ResolvePath(string path)
    {
        path = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(path)!;
        foreach (var part in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            if (Directory.Exists(resolved))
                resolved = Directory.ResolveLinkTarget(resolved, returnFinalTarget: true)?.FullName ?? resolved;
            else if (File.Exists(resolved))
                resolved = File.ResolveLinkTarget(resolved, returnFinalTarget: true)?.FullName ?? resolved;
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Castar Server supervisor paths cannot be links.");
    }

    private sealed record Child(Process Process, string StopMarker);
    private sealed record HealthReport(string Status, string Version, int ProcessId);
}
