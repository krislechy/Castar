using System.ComponentModel;
using System.Runtime.InteropServices;
using Castar.Updater;

const string Usage = "Castar.Updater.exe <installDirectory> <payloadDirectory> [parentProcessId] [--no-restart]";
var restart = args.LastOrDefault() != "--no-restart";
var values = restart ? args : args[..^1];
int? parentId = null;
if (!OperatingSystem.IsWindows() || values.Length is < 2 or > 3
    || (values.Length == 3 && (!int.TryParse(values[2], out var processId) || processId <= 0)))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

if (values.Length == 3)
    parentId = int.Parse(values[2]);

try
{
    var update = new ClientUpdate(values[0], values[1]);
    update.Apply(parentId, restart);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
    or InvalidOperationException or Win32Exception or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(error);
    if (restart)
    {
        var message = "Не удалось обновить Castar. Закройте приложение и повторите попытку.";
        try
        {
            var log = Path.Combine(Path.GetTempPath(), $"Castar.Update.{Environment.ProcessId}.log");
            File.WriteAllText(log, error.ToString());
            message += $"\n\nПодробности: {log}";
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(logError);
        }
        ShowError(IntPtr.Zero, message, "Castar", 0x10);
    }
    return 1;
}

[DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
static extern int ShowError(IntPtr parentWindow, string text, string caption, uint type);
