using System.Runtime.InteropServices;

namespace Castar.Server;

internal static class UpdateStatusFileObserver
{
    public static Task RunAsync(string directory, Action signal, CancellationToken token) =>
        OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()
            ? Task.Factory.StartNew(() => ObserveDarwin(directory, signal, token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : ObserveDesktopAsync(directory, signal, token);

    private static async Task ObserveDesktopAsync(string directory, Action signal, CancellationToken token)
    {
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, "update.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, _) => signal();
        watcher.Changed += (_, _) => signal();
        watcher.Deleted += (_, _) => signal();
        watcher.Renamed += (_, _) => signal();
        watcher.Error += (_, args) =>
        {
            signal();
            if (args.GetException() is not InternalBufferOverflowException)
                failure.TrySetException(args.GetException());
        };
        watcher.EnableRaisingEvents = true;
        signal();
        await failure.Task.WaitAsync(token);
    }

    private static void ObserveDarwin(string directory, Action signal, CancellationToken token)
    {
        var descriptor = Open(directory, OpenEventsOnly | CloseOnExec);
        if (descriptor < 0)
            throw NativeError("open");
        try
        {
            var queue = Kqueue();
            if (queue < 0)
                throw NativeError("kqueue");
            try
            {
                Register(queue, new DarwinEvent((ulong)descriptor, VnodeFilter, AddClear, DirectoryEvents));
                Register(queue, new DarwinEvent(CancellationEvent, UserFilter, AddClear, 0));
                using var stop = token.UnsafeRegister(static state =>
                {
                    var queue = (int)state!;
                    var item = new DarwinEvent(CancellationEvent, UserFilter, 0, TriggerUserEvent);
                    while (Submit(queue, ref item, 1, 0, 0, 0, 0) < 0 && Marshal.GetLastPInvokeError() == Interrupted)
                    {
                    }
                }, queue);
                signal();

                while (!token.IsCancellationRequested)
                {
                    var count = Receive(queue, 0, 0, out var result, 1, 0, 0);
                    if (count < 0 && Marshal.GetLastPInvokeError() != Interrupted)
                        throw NativeError("kevent64");
                    if (count <= 0)
                        continue;

                    if ((result.Flags & EventError) != 0)
                        throw new IOException($"Darwin status notification failed (errno {result.Data}).");
                    if (result.Filter == VnodeFilter)
                    {
                        if ((result.FilterFlags & DirectoryUnavailable) != 0)
                            throw new IOException("The Darwin update status directory was removed.");
                        signal();
                    }
                }
            }
            finally
            {
                Close(queue);
            }
        }
        finally
        {
            Close(descriptor);
        }
    }

    private static void Register(int queue, DarwinEvent item)
    {
        while (Submit(queue, ref item, 1, 0, 0, 0, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() != Interrupted)
                throw NativeError("kevent64");
        }
    }

    private static IOException NativeError(string operation) =>
        new($"Darwin {operation} failed (errno {Marshal.GetLastPInvokeError()}).");

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int OpenEventsOnly = 0x8000;
    private const int CloseOnExec = 0x01000000;
    private const int Interrupted = 4;
    private const short VnodeFilter = -4;
    private const short UserFilter = -10;
    private const ushort AddClear = 0x0021;
    private const ushort EventError = 0x4000;
    private const uint DirectoryUnavailable = 0x0061;
    private const uint DirectoryEvents = DirectoryUnavailable | 0x0002;
    private const ulong CancellationEvent = 1;
    private const uint TriggerUserEvent = 0x01000000;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DarwinEvent(ulong identifier, short filter, ushort flags, uint filterFlags)
    {
        public readonly ulong Identifier = identifier;
        public readonly short Filter = filter;
        public readonly ushort Flags = flags;
        public readonly uint FilterFlags = filterFlags;
        public readonly long Data = 0;
        public readonly ulong UserData = 0;
        public readonly ulong Extension0 = 0;
        public readonly ulong Extension1 = 0;
    }

    [DllImport(LibSystem, EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(LibSystem, EntryPoint = "close")]
    private static extern int Close(int descriptor);

    [DllImport(LibSystem, EntryPoint = "kqueue", SetLastError = true)]
    private static extern int Kqueue();

    [DllImport(LibSystem, EntryPoint = "kevent64", SetLastError = true)]
    private static extern int Submit(int queue, ref DarwinEvent changes, int changeCount, nint events,
        int eventCount, uint flags, nint timeout);

    [DllImport(LibSystem, EntryPoint = "kevent64", SetLastError = true)]
    private static extern int Receive(int queue, nint changes, int changeCount, out DarwinEvent events,
        int eventCount, uint flags, nint timeout);
}
