using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Castar.Api;
using Castar.Updates;

namespace Castar.Server;

internal static class ServerUpdateStaging
{
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private const long MaxUnpackedBytes = 2 * MaxArchiveBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static Version InstalledVersion { get; } =
        GitHubReleaseAssetService.ReadInstalledVersion(typeof(ServerUpdateStaging).Assembly);

    internal static async Task<int> RunAsync(string work, string statusPath, HttpMessageHandler? handler = null,
        Version? installedVersion = null, CancellationToken cancellationToken = default)
    {
        var current = installedVersion ?? InstalledVersion;
        var status = new ServerUpdateStatusDto("checking", current.ToString());
        try
        {
            WriteStatus(statusPath, status);
            var packageName = PackageName();
            using var releases = new GitHubReleaseAssetService(packageName, current, handler, MaxArchiveBytes);
            var release = await releases.CheckAsync(cancellationToken);
            if (release is null)
            {
                WriteStatus(statusPath, status with { State = "current" });
                return 3;
            }

            status = status with { State = "downloading", Version = release.Version.ToString(), TotalBytes = release.SizeBytes };
            WriteStatus(statusPath, status);
            var archive = await releases.DownloadAsync(release, cancellationToken, new UpdateProgress(bytes =>
            {
                status = status with { DownloadedBytes = bytes };
                WriteStatus(statusPath, status);
            }), destinationDirectory: work);
            var payload = Path.Combine(work, "payload");
            if (Directory.Exists(payload))
                throw new InvalidDataException("Каталог обновления уже существует.");
            Directory.CreateDirectory(payload);
            await ExtractAsync(archive, payload, cancellationToken);
            VerifyPayload(payload, release.Version);
            File.WriteAllText(Path.Combine(work, "version"), release.Version.ToString());
            File.Delete(archive);
            WriteStatus(statusPath, status with { State = "installing" });
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or BadImageFormatException or
            HttpRequestException or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Castar update staging failed ({error.GetType().Name}).");
            var message = error is InvalidDataException ? error.Message : "Не удалось подготовить обновление сервера.";
            WriteStatus(statusPath, status with { State = "failed", Error = message });
            return 1;
        }
    }

    private static string PackageName()
    {
        if (OperatingSystem.IsIOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "Castar-Server-ios-arm64.tar";
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "Castar-Server-win-x64.zip";
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "Castar-Server-linux-x64.tar.gz",
                Architecture.Arm64 => "Castar-Server-linux-arm64.tar.gz",
                _ => throw new PlatformNotSupportedException()
            };
        }
        throw new PlatformNotSupportedException();
    }

    internal static void WriteStatus(string path, ServerUpdateStatusDto status)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async Task ExtractAsync(string archive, string payload, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(archive);
        long remaining = MaxUnpackedBytes;
        var entries = 0;
        var copyBuffer = new byte[64 * 1024];
        if (archive.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if ((entry.ExternalAttributes >>> 16 & 0xf000) == 0xa000)
                    throw new InvalidDataException("Архив обновления содержит символьную ссылку.");
                await using var data = entry.Open();
                await ExtractEntry(entry.FullName, entry.Name.Length == 0, data, entry.Length);
            }
            return;
        }

        await using var decompression = archive.EndsWith(".gz", StringComparison.Ordinal)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : null;
        await using var tar = new TarReader((Stream?)decompression ?? file);
        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Архив обновления содержит неподдерживаемый файл.");
            var destination = await ExtractEntry(entry.Name, entry.EntryType == TarEntryType.Directory,
                entry.DataStream, entry.Length);
            if (!OperatingSystem.IsWindows() && destination is not null)
            {
                const UnixFileMode mask = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                     UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(destination, (entry.Mode & mask) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        return;

        async Task<string?> ExtractEntry(string name, bool directory, Stream? content, long size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > 10000 || size < 0 || size > remaining || name.StartsWith('/') || name.Contains('\\') ||
                name.Contains(':') || name.Contains('\0'))
                throw new InvalidDataException("Архив обновления имеет недопустимую структуру.");

            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
            if (segments.Any(part => part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Архив обновления содержит недопустимый путь.");
            if (segments.Length == 0 && directory)
                return null;
            if (segments.Length == 0)
                throw new InvalidDataException("В архиве обновления отсутствует имя файла.");

            var target = Path.GetFullPath(Path.Combine(payload, Path.Combine(segments)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!target.StartsWith(Path.GetFullPath(payload) + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("Файл выходит за пределы каталога обновления.");
            if (directory)
            {
                Directory.CreateDirectory(target);
                return null;
            }
            remaining -= size;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            if (content is not null)
            {
                while (await content.ReadAsync(copyBuffer, cancellationToken) is var count and > 0)
                {
                    if (count > size - output.Position)
                        throw new InvalidDataException("Файл в архиве обновления больше указанного размера.");
                    await output.WriteAsync(copyBuffer.AsMemory(0, count), cancellationToken);
                }
            }
            if (output.Length != size)
                throw new InvalidDataException("Файл в архиве обновления загружен не полностью.");
            return target;
        }
    }

    private static void VerifyPayload(string payload, Version releaseVersion)
    {
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(payload, "Castar.Server.dll"));
        if (assembly.Name != "Castar.Server" || assembly.Version is not { } version || version.Build < 0 ||
            new Version(version.Major, version.Minor, version.Build) != releaseVersion)
            throw new InvalidDataException("Версия сервера в архиве не совпадает с версией выпуска.");

        if (OperatingSystem.IsWindows())
        {
            using var executable = File.OpenRead(Path.Combine(payload, "Castar.Server.exe"));
            using var headers = new PEReader(executable);
            if (headers.PEHeaders.CoffHeader.Machine == Machine.Amd64)
                return;
        }
        else
        {
            Span<byte> header = stackalloc byte[20];
            using var executable = File.OpenRead(Path.Combine(payload, OperatingSystem.IsIOS() ? "castar-host" : "Castar.Server"));
            executable.ReadExactly(header);
            if (OperatingSystem.IsIOS() && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf &&
                BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 0x0100000c)
            {
                if (File.ReadAllText(Path.Combine(payload, "version")).Trim() == releaseVersion.ToString())
                    return;
                throw new InvalidDataException("Версия сервера в архиве iPhone отличается от версии выпуска.");
            }
            if (OperatingSystem.IsLinux() && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x464c457f &&
                header[4] == 2 && header[5] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) ==
                (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 183 : 62))
                return;
        }
        throw new InvalidDataException("Архив обновления предназначен для другой платформы.");
    }

    private sealed class UpdateProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
