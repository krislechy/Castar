using Castar.Settings;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Castar.Notifications;

internal static class NotificationPlacement
{
    internal const int MarginDip = 12;

    private const int ExtendedStyle = -20;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int MouseActivateMessage = 0x0021;
    private const int NoActivateResult = 3;
    private const uint NearestMonitor = 2;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private const uint KeepOrdering = NoZOrder | NoActivate | NoOwnerZOrder;

    // Use per-monitor coordinates for the auxiliary window without changing the host window's DPI mode.
    internal readonly ref struct PerMonitorScope
    {
        private readonly nint previous;

        public PerMonitorScope() => previous = SetThreadDpiAwarenessContext(-4);
        public void Dispose()
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    internal static void ProtectFocus(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | ToolWindowStyle | NoActivateStyle);
        HwndSource.FromHwnd(handle)?.AddHook(NoActivateOnClick);
    }

    private static nint NoActivateOnClick(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != MouseActivateMessage)
            return 0;
        handled = true;
        return NoActivateResult;
    }

    internal static WorkArea FindWorkArea(Window owner, nint toastHandle)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(owner).Handle, NearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            var fallbackScale = GetScale(toastHandle);
            return new((int)(fallback.Left * fallbackScale), (int)(fallback.Top * fallbackScale),
                (int)(fallback.Right * fallbackScale), (int)(fallback.Bottom * fallbackScale), fallbackScale);
        }

        var rect = info.Work;
        if (MonitorFromWindow(toastHandle, NearestMonitor) != monitor)
        {
            // Move inside the target monitor first so Windows can report its DPI for this HWND.
            var x = rect.Left + (rect.Right - rect.Left) / 4;
            var y = rect.Top + (rect.Bottom - rect.Top) / 4;
            SetWindowPos(toastHandle, 0, x, y, 0, 0, NoSize | KeepOrdering);
        }
        return new(rect.Left, rect.Top, rect.Right, rect.Bottom, GetScale(toastHandle));
    }

    internal static void Position(nint handle, WorkArea area, NotificationCorner corner, double widthDip, double heightDip)
    {
        var margin = (int)Math.Round(MarginDip * area.Scale);
        var width = (int)Math.Round(widthDip * area.Scale);
        var height = (int)Math.Round(heightDip * area.Scale);
        var left = corner is NotificationCorner.TopLeft or NotificationCorner.BottomLeft;
        var top = corner is NotificationCorner.TopLeft or NotificationCorner.TopRight;
        var x = left ? area.Left + margin : Math.Max(area.Left, area.Right - margin - width);
        var y = top ? area.Top + margin : Math.Max(area.Top, area.Bottom - margin - height);
        SetWindowPos(handle, 0, x, y, width, height, KeepOrdering);
    }

    private static double GetScale(nint handle)
    {
        var dpi = GetDpiForWindow(handle);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    internal readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, double Scale)
    {
        internal double WidthDip => (Right - Left) / Scale;
        internal double HeightDip => (Bottom - Top) / Scale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint handle, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
