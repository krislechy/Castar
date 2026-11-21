using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Castar.Compontents;

public partial class TrayContextMenu : ContextMenu
{
    private readonly Action? open;
    private readonly Action exit;

    internal TrayContextMenu(Action? open, Action exit)
    {
        this.open = open;
        this.exit = exit;
        InitializeComponent();
        OpenItem.IsEnabled = open is not null;
    }

    internal void ShowAtPointer()
    {
        IsOpen = false;
        IsOpen = true;
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        // Explorer owns the foreground at the tray. Activate the popup so it can receive keys and dismiss on focus loss.
        if (PresentationSource.FromVisual(this) is HwndSource source)
            SetForegroundWindow(source.Handle);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => open?.Invoke();

    private void Exit_Click(object sender, RoutedEventArgs e) => exit();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
