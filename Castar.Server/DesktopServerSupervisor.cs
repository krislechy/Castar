using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Castar.Api;

namespace Castar.Server;

internal sealed class DesktopServerSupervisor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(250);
    private readonly string current;
    private readonly string control;
    private readonly string request;
    private readonly string claimedRequest;
    private readonly string statusPath;
    private readonly string journalPath;
    private readonly string activationGate;
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
        request = Path.Combine(control, "request");
        claimedRequest = Path.Combine(control, "request.in-progress");
        statusPath = Path.Combine(control, "update.json");
        journalPath = Path.Combine(control, "transaction");
        activationGate = Path.Combine(control, "activate");
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
        var recovered = Recover();
        File.Delete(activationGate);
        File.Delete(claimedRequest);
        if (recovered)
            File.Delete(request);
        var version = ReadVersion(current);
        WriteStatus(new(recovered ? "failed" : "idle", version.ToString(),
            Error: recovered ? "Прерванное обновление отменено. Восстановлена прежняя версия." : null));

        try
        {
            await StartAsync(version, verification: false, token);
            while (true)
            {
                await Task.Delay(PollDelay, token);
                if (child!.Process.HasExited)
                {
                    await StopAsync();
                    Console.Error.WriteLine("Castar Server exited. Restarting the supervised process.");
                    await StartAsync(version, verification: false, token);
                }
                if (ClaimRequest(version))
                {
                    try
                    {
                        version = await UpdateAsync(version, token);
                    }
                    finally
                    {
                        File.Delete(claimedRequest);
                    }
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

    private bool ClaimRequest(Version installed)
    {
        try
        {
            var attributes = File.GetAttributes(request);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            if (new FileInfo(request).Length != 0)
            {
                File.Delete(request);
                return false;
            }
            // Block duplicate API requests before moving the marker out of its public slot.
            ServerUpdateStaging.WriteStatus(statusPath, new("checking", installed.ToString()));
            File.Move(request, claimedRequest);
            return true;
        }
        catch (IOException)
        {
            // The server may still be closing the newly created request marker.
            return false;
        }
    }

    private async Task<Version> UpdateAsync(Version installed, CancellationToken token)
    {
        var work = Path.Combine(control, $".update-{Guid.NewGuid():N}");
        var status = new ServerUpdateStatusDto("checking", installed.ToString());
        var committed = false;
        try
        {
            Directory.CreateDirectory(work);
            var result = await ServerUpdateStaging.RunAsync(work, statusPath, installedVersion: installed, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (result != 0)
                return installed;

            var payload = Path.Combine(work, "payload");
            var version = ReadVersion(payload);
            status = ReadStatus() with { State = "installing", InstalledVersion = installed.ToString(), Version = version.ToString() };
            await StopAsync();
            token.ThrowIfCancellationRequested();
            await BackupDataAsync(work, token);
            token.ThrowIfCancellationRequested();
            BeginTransaction(work);
            Directory.Move(current, Path.Combine(work, "previous"));
            Directory.Move(payload, current);
            await StartAsync(version, verification: true, token);
            await StopAsync();
            using (new FileStream(activationGate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            await StartAsync(version, verification: false, token, requiresActivation: true);
            token.ThrowIfCancellationRequested();
            File.Delete(journalPath);
            committed = true;
            File.Delete(activationGate);
            WriteStatus(status with { State = "complete", InstalledVersion = version.ToString() });
            return version;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            JsonException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or BadImageFormatException)
        {
            Console.Error.WriteLine($"Castar Server update failed: {error.Message}");
            if (committed)
                throw;
            if (File.Exists(journalPath))
            {
                await StopAsync();
                Recover();
            }

            File.Delete(activationGate);
            WriteStatus(status with { State = "failed", Error = "Не удалось установить обновление сервера." });
            token.ThrowIfCancellationRequested();
            if (child is null)
                await StartAsync(installed, verification: false, token);
            return installed;
        }
        finally
        {
            if (!File.Exists(journalPath))
                CleanUp(work);
        }
    }

    private ServerUpdateStatusDto ReadStatus() =>
        JsonSerializer.Deserialize<ServerUpdateStatusDto>(File.ReadAllBytes(statusPath), JsonOptions)
        ?? throw new IOException("The update status file is empty.");

    private void BeginTransaction(string work)
    {
        var temporary = Path.Combine(work, "transaction.new");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateTransaction(Path.GetFileName(work), databasePath), JsonOptions);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, journalPath);
    }

    private bool Recover()
    {
        if (!File.Exists(journalPath))
            return false;
        var transaction = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllBytes(journalPath), JsonOptions);
        if (transaction?.WorkName is not { } name || !IsWorkName(name))
            throw new IOException("Invalid Castar Server update journal.");
        if (transaction.DatabasePath is not { } snapshotPath || !Path.IsPathFullyQualified(snapshotPath)
            || !SamePath(ResolvePath(snapshotPath), databasePath))
            throw new IOException("Use the original Database__SqlitePath to recover the interrupted update.");

        var work = Path.Combine(control, name);
        if (!Directory.Exists(work))
            throw new IOException("The Castar Server update recovery directory is missing.");
        RejectLink(work);

        var previous = Path.Combine(work, "previous");
        if (Directory.Exists(previous))
        {
            if (Directory.Exists(current))
                Directory.Move(current, Path.Combine(work, "failed"));
            RestoreData(work);
            Directory.Move(previous, current);
        }
        else if (!Directory.Exists(current))
        {
            throw new IOException("The previous Castar Server files are missing.");
        }

        File.Delete(journalPath);
        CleanUp(work);
        return true;
    }

    private string[] DataFiles() =>
        [databasePath, databasePath + "-wal", databasePath + "-shm", databasePath + "-journal",
            databasePath + ".keys.json", databasePath + ".settings.json"];

    private async Task BackupDataAsync(string work, CancellationToken token)
    {
        var backup = Path.Combine(work, "data");
        Directory.CreateDirectory(backup);
        var files = DataFiles();
        for (var i = 0; i < files.Length; i++)
        {
            var source = files[i];
            if (File.Exists(source))
            {
                RejectLink(source);
                var destination = Path.Combine(backup, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var input = File.OpenRead(source);
                using var output = ServerSettings.CreatePrivateFile(destination);
                await input.CopyToAsync(output, token);
                FinishCopy(source, destination, output);
            }
        }
    }

    private void RestoreData(string work)
    {
        var backup = Path.Combine(work, "data");
        if (!Directory.Exists(backup))
            throw new IOException("The SQLite backup for Castar Server is missing.");
        var files = DataFiles();
        for (var i = 0; i < files.Length; i++)
        {
            var snapshot = Path.Combine(backup, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var destination = files[i];
            if (!File.Exists(snapshot))
            {
                File.Delete(destination);
                continue;
            }

            var temporary = destination + ".restore-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDurable(snapshot, temporary, destination.EndsWith(".settings.json", StringComparison.Ordinal));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static void CopyDurable(string source, string destination, bool privateSettings)
    {
        using var input = File.OpenRead(source);
        using var output = ServerSettings.CreatePrivateFile(destination);
        input.CopyTo(output);
        if (privateSettings)
            output.Flush(flushToDisk: true);
        else
            FinishCopy(source, destination, output);
    }

    private static void FinishCopy(string source, string destination, FileStream output)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        output.Flush(flushToDisk: true);
    }

    private async Task StartAsync(Version version, bool verification, CancellationToken token, bool requiresActivation = false)
    {
        token.ThrowIfCancellationRequested();
        var instance = Guid.NewGuid();
        var executable = Path.Combine(current, OperatingSystem.IsWindows() ? "Castar.Server.exe" : "Castar.Server");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = current };
        info.Environment[DesktopServerChild.ControlVariable] = control;
        info.Environment[DesktopServerChild.OwnerVariable] = $"{owner}:{instance:N}";
        info.Environment["Database__SqlitePath"] = databasePath;
        info.Environment["Castar__UpdateVerification"] = verification ? "true" : "false";
        const string gateVariable = "Castar__UpdateActivationGate";
        if (requiresActivation)
            info.Environment[gateVariable] = activationGate;
        else
            info.Environment.Remove(gateVariable);
        var target = health;
        if (verification)
        {
            foreach (var key in info.Environment.Keys.ToArray())
            {
                if (key.Replace("__", ":").StartsWith("Kestrel:Endpoints:", StringComparison.OrdinalIgnoreCase))
                    info.Environment.Remove(key);
            }
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            target = new Uri($"http://127.0.0.1:{port}/health");
            info.ArgumentList.Add("--urls");
            info.ArgumentList.Add(target.GetLeftPart(UriPartial.Authority));
        }

        child = new(Process.Start(info) ?? throw new InvalidOperationException("Castar Server could not be started."),
            DesktopServerChild.StopMarker(control, instance));
        await WaitUntilHealthyAsync(child.Process, target, version, token);
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

    private void WriteStatus(ServerUpdateStatusDto status)
    {
        try { ServerUpdateStaging.WriteStatus(statusPath, status); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write Castar Server update status.");
        }
    }

    private void CleanUp(string directory)
    {
        if (!SamePath(Path.GetDirectoryName(directory), control) || !IsWorkName(Path.GetFileName(directory))
            || !SamePath(ResolvePath(directory), directory))
            throw new IOException("Invalid Castar Server update work directory.");
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not remove a Castar Server update work directory.");
        }
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

    private static bool IsWorkName(string name) =>
        name.StartsWith(".update-", StringComparison.Ordinal) && Guid.TryParseExact(name[8..], "N", out _);

    private static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
            throw new IOException("Castar Server update paths cannot be links.");
    }

    private sealed record Child(Process Process, string StopMarker);
    private sealed record HealthReport(string Status, string Version, int ProcessId);
    private sealed record UpdateTransaction(string WorkName, string DatabasePath);
}
