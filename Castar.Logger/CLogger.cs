using System.Diagnostics;
using System.Globalization;
using System.Security;

namespace Castar.Logger;

public sealed class CLogger(string filePath) : ICLogger
{
    private readonly object gate = new();

    public void Log<T>(string message, TypeLog type) where T : class
    {
        var time = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var entry = $"[{time}] [PID {Environment.ProcessId}] <{typeof(T).Name}> {type}: {message}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                // A terminating process cannot wait for a fire-and-forget write.
                File.AppendAllText(filePath, entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), $"Castar-crash-{Environment.ProcessId}.log"), entry);
                }
                catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException or SecurityException)
                {
                    Trace.TraceError(entry);
                }
            }
        }
    }
}
