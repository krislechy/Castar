using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Castar.Updater;

internal sealed class ClientUpdate
{
    private const string Manifest = "Castar.UpdateFiles.txt";
    private const string TransactionName = ".castar-update";
    private const string CleanupName = TransactionName + "-cleanup";
    private static readonly string[] MainFiles =
        ["Castar.exe", "Castar.dll", "Castar.deps.json", "Castar.runtimeconfig.json"];

    private readonly string installation;
    private readonly string payload;
    private readonly string state;
    private readonly string cleanup;
    private readonly string executable;

    internal ClientUpdate(string installDirectory, string payloadDirectory)
    {
        installation = NormalizeDirectory(installDirectory);
        payload = NormalizeDirectory(payloadDirectory);
        state = Path.Combine(installation, TransactionName);
        cleanup = Path.Combine(installation, CleanupName);
        executable = Path.Combine(installation, "Castar.exe");

        if (Contains(installation, payload) || Contains(payload, installation)
            || Contains(installation, AppContext.BaseDirectory) || Contains(installation, Environment.CurrentDirectory))
        {
            throw new ArgumentException("Updater and payload must run outside the installation directory.");
        }
    }

    internal void Apply(int? parentId, bool restart)
    {
        if (parentId is { } id)
            WaitForParent(id);

        var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ToUpperInvariant())));
        using var mutex = new Mutex(false, $@"Local\Castar.Update.{lockKey}");
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException("Another Castar update is already running.");
        }
        catch (AbandonedMutexException)
        {
            // The previous updater may have been interrupted. Recover below with the lock held.
        }

        try
        {
            EnsureClientClosed();
            Recover();
            ReplaceFiles();
            if (restart)
            {
                using var application = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = installation })
                    ?? throw new InvalidOperationException("The updated Castar client could not be restarted.");
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void ReplaceFiles()
    {
        var newFiles = ReadManifest(payload);
        if (MainFiles.Any(file => !File.Exists(SafePath(installation, file)) || !newFiles.Contains(file)))
            throw new InvalidDataException("The installation and update must both contain the Castar application.");

        var oldFiles = File.Exists(SafePath(installation, Manifest))
            ? ReadManifest(installation)
            : LegacyFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = newFiles.Concat(oldFiles).Append(Manifest).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => new UpdateFile(file, File.Exists(SafePath(installation, file)))).ToArray();
        Directory.CreateDirectory(state);
        var incoming = Path.Combine(state, "incoming");
        var backup = Path.Combine(state, "backup");
        try
        {
            foreach (var file in newFiles.Append(Manifest))
            {
                var destination = SafePath(incoming, file);
                CreateParent(destination);
                File.Copy(SafePath(payload, file), destination);
            }
            WriteState("journal.json", JsonSerializer.Serialize(changes));

            foreach (var (file, existed) in changes)
            {
                var installed = SafePath(installation, file);
                if (existed)
                {
                    var saved = SafePath(backup, file);
                    CreateParent(saved);
                    File.Move(installed, saved);
                }

                var candidate = SafePath(incoming, file);
                if (File.Exists(candidate))
                {
                    CreateParent(installed);
                    File.Move(candidate, installed);
                }
            }

            WriteState("complete", "");
        }
        catch (Exception applyError)
        {
            try
            {
                Recover();
            }
            catch (Exception recoveryError)
            {
                throw new IOException("Update rollback is incomplete. Start the updater again with the client closed.",
                    new AggregateException(applyError, recoveryError));
            }
            throw;
        }

        // Installed files are already committed. Cleanup can be retried next time if temporarily locked.
        try
        {
            ResetState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
        }
    }

    private void Recover()
    {
        DeleteStateDirectory(cleanup);
        if (!Directory.Exists(state))
            return;

        var journal = SafePath(installation, $"{TransactionName}/journal.json");
        var committed = SafePath(installation, $"{TransactionName}/complete");
        if (File.Exists(journal) && !File.Exists(committed))
        {
            var changes = JsonSerializer.Deserialize<UpdateFile[]>(File.ReadAllText(journal))
                ?? throw new InvalidDataException("The update journal is invalid.");
            foreach (var (name, existed) in changes.Reverse())
            {
                var file = name == Manifest ? Manifest : ValidateFile(name);
                var installed = SafePath(installation, file);
                var saved = SafePath(Path.Combine(state, "backup"), file);
                if (File.Exists(saved))
                {
                    CreateParent(installed);
                    File.Move(saved, installed, overwrite: true);
                }
                else if (!existed)
                {
                    File.Delete(installed);
                }
            }
        }

        ResetState();
    }

    private void WriteState(string name, string content)
    {
        var destination = Path.Combine(state, name);
        var temporary = destination + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination);
    }

    private void ResetState()
    {
        if (!Directory.Exists(state))
            return;

        EnsurePlainDirectory(state);
        // Remove the commit marker and journal from recoverable state together before cleanup.
        Directory.Move(state, cleanup);
        DeleteStateDirectory(cleanup);
    }

    private void DeleteStateDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        if (!Contains(installation, directory) || PathsEqual(installation, directory))
            throw new InvalidOperationException("Invalid update transaction directory.");
        EnsurePlainDirectory(directory);
        Directory.Delete(directory, recursive: true);
    }

    private IEnumerable<string> LegacyFiles()
    {
        // Self-contained releases before v0.3.0 have no file manifest. Their deps file lists
        // installed assemblies, native libraries and localized resources for the active RID.
        using var manifest = JsonDocument.Parse(File.ReadAllText(SafePath(installation, "Castar.deps.json")));
        var root = manifest.RootElement;
        if (root.TryGetProperty("runtimeTarget", out var runtime) && runtime.TryGetProperty("name", out var name)
            && name.GetString() is { } targetName && root.TryGetProperty("targets", out var targets)
            && targets.TryGetProperty(targetName, out var target))
        {
            foreach (var package in target.EnumerateObject())
            foreach (var section in new[] { "runtime", "native", "resources" })
            {
                if (!package.Value.TryGetProperty(section, out var assets))
                    continue;

                foreach (var asset in assets.EnumerateObject())
                {
                    var file = Path.GetFileName(asset.Name);
                    if (file == "_._")
                        continue;
                    if (section == "resources" && asset.Value.TryGetProperty("locale", out var locale)
                        && locale.GetString() is { Length: > 0 } culture)
                        file = Path.Combine(culture, file);
                    yield return ValidateFile(file);
                }
            }
        }

        // A framework-dependent app must start through the shared .NET host.
        foreach (var file in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll" })
            yield return file;
    }

    private static HashSet<string> ReadManifest(string root)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(SafePath(root, Manifest)))
        {
            if (!files.Add(ValidateFile(line)))
                throw new InvalidDataException("The update manifest contains a duplicate file.");
        }
        return files;
    }

    private static string ValidateFile(string name)
    {
        var segments = name.Split(['/', '\\']);
        if (segments.Any(segment => segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(segment)))
        {
            throw new InvalidDataException("The update manifest contains an invalid file path.");
        }

        var file = segments[^1];
        if (segments[0].StartsWith(TransactionName, StringComparison.OrdinalIgnoreCase)
            || file.Equals(Manifest, StringComparison.OrdinalIgnoreCase)
            || file.Equals("Castar.Settings.xml", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Castar.Client.xml", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || file.Contains(".sqlite", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update manifest lists a protected file.");
        }
        return Path.Combine(segments);
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.', 2)[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    private static string SafePath(string root, string file)
    {
        var path = Path.GetFullPath(Path.Combine(root, file));
        if (!Contains(root, path) || PathsEqual(root, path))
            throw new InvalidDataException("The update path escapes its directory.");

        // Check existing directories as well as the file so junctions cannot redirect a write.
        for (var current = path; Contains(root, current); current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Update paths must not be symbolic links or junctions.");
            if (PathsEqual(root, current))
                break;
        }
        if (Directory.Exists(path))
            throw new IOException($"The update expected a file at {path}.");
        return path;
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (PathsEqual(fullPath, Path.GetPathRoot(fullPath) ?? ""))
            throw new ArgumentException("A volume root is not a valid update directory.");
        EnsurePlainDirectory(fullPath);
        return fullPath;
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new DirectoryNotFoundException("An update directory is missing or is a reparse point.");
    }

    private static void CreateParent(string path) => Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second),
            StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string root, string path) => PathsEqual(root, path)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void WaitForParent(int id)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        {
            if (process.HasExited)
                return;
            if (process.MainModule?.FileName is not { } path || !PathsEqual(path, executable))
                throw new ArgumentException("The restart process does not match the Castar installation.");
            if (!process.WaitForExit(60_000))
                throw new InvalidOperationException("Castar is still running.");
        }
    }

    private void EnsureClientClosed()
    {
        foreach (var process in Process.GetProcessesByName("Castar"))
        {
            using (process)
            {
                if (!process.HasExited && process.MainModule?.FileName is { } path && PathsEqual(path, executable))
                    throw new InvalidOperationException("Close Castar before applying the update.");
            }
        }
    }

    private sealed record UpdateFile(string Name, bool Existed);
}
