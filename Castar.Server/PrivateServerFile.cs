using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Castar.Server;

internal static class PrivateServerFile
{
    internal static async Task ReplaceJsonAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            await using (var file = Create(temporary, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await JsonSerializer.SerializeAsync(file, value, options, token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original write failure. The staging file is private.
                }
            }
        }
    }

    internal static FileStream Create(string path, FileOptions options = FileOptions.None)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var account = identity.User ?? throw new InvalidOperationException("Cannot identify the Castar Server Windows account.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
                4096, options, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = options,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
}
