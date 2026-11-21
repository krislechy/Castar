using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Castar.LocalAi;

internal sealed class ManagedLocalAiHost : IDisposable
{
    private readonly Process process;

    internal sealed record Launch(LocalAiProvider Provider, string Executable, string? Directory, string? Model, string? ModelFile);

    internal ManagedLocalAiHost(Launch launch, EventHandler onExited)
    {
        Configuration = launch;
        var port = FindLoopbackPort();
        Endpoint = new Uri(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
        var info = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launch.Executable)!
        };

        if (launch.Provider == LocalAiProvider.Ollama)
        {
            info.ArgumentList.Add("serve");
            info.Environment["OLLAMA_HOST"] = Endpoint.GetLeftPart(UriPartial.Authority);
            info.Environment["OLLAMA_NO_CLOUD"] = "1";
            if (launch.Directory is not null)
                info.Environment["OLLAMA_MODELS"] = launch.Directory;
        }
        else
        {
            string[] args = ["--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
                "--model", launch.ModelFile!, "--alias", launch.Model!, "--ctx-size", "8192", "--parallel", "1"];
            foreach (var argument in args)
                info.ArgumentList.Add(argument);
        }

        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += onExited;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить сервер локальной модели.");
        }
        catch (Win32Exception error)
        {
            process.Dispose();
            throw new InvalidOperationException("Не удалось запустить сервер локальной модели. Проверьте файл запуска.", error);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal Launch Configuration { get; }
    internal Uri Endpoint { get; }

    internal bool IsRunning
    {
        get
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal static Launch Resolve(LocalAiOptions options)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("На этой платформе доступно только подключение к HTTP API модели.");

        var executable = FindExecutable(options);
        if (options.Provider == LocalAiProvider.Ollama)
            return new(options.Provider, executable, string.IsNullOrWhiteSpace(options.ModelDirectory)
                ? null : ResolveDirectory(options.ModelDirectory), null, null);

        var directory = ResolveDirectory(options.ModelDirectory);
        if (string.IsNullOrWhiteSpace(options.Model) || Path.GetFileName(options.Model) != options.Model
            || !IsGgufModel(options.Model))
            throw new ArgumentException("Выберите модель GGUF из указанной папки.");
        var modelPath = Path.Combine(directory, options.Model);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Выбранная модель GGUF отсутствует в указанной папке.");
        return new(options.Provider, executable, directory, options.Model, modelPath);
    }

    internal static string[] DiscoverGguf(string? directory) => Directory.EnumerateFiles(ResolveDirectory(directory))
        .Select(path => Path.GetFileName(path)).Where(IsGgufModel)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsGgufModel(string name) => name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);

    private static string ResolveDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException("Выберите существующую локальную папку моделей.");
        return Path.GetFullPath(path);
    }

    private static string FindExecutable(LocalAiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (!Path.IsPathFullyQualified(options.ExecutablePath) || !File.Exists(options.ExecutablePath))
                throw new FileNotFoundException("Выберите существующий исполняемый файл локального сервера.");
            return Path.GetFullPath(options.ExecutablePath);
        }

        var name = options.Provider == LocalAiProvider.Ollama ? "ollama" : "llama-server";
        if (OperatingSystem.IsWindows())
            name += ".exe";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified);
        if (OperatingSystem.IsWindows() && options.Provider == LocalAiProvider.Ollama)
            paths = paths.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Ollama"));

        return paths.Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Сервер {name} не найден. Укажите путь к исполняемому файлу.");
    }

    private static int FindLoopbackPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.ExclusiveAddressUse = true;
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        finally
        {
            process.Dispose();
        }
    }
}
