using System;
using System.Windows.Input;

namespace Castar.Compontents;

internal static class TabNavigationBlock
{
    [ThreadStatic]
    private static bool installed;

    internal static void Install()
    {
        if (installed)
            return;
        installed = true;
        InputManager.Current.PreProcessInput += BlockTab;
    }

    private static void BlockTab(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs { Key: Key.Tab } key
            && (key.RoutedEvent == Keyboard.PreviewKeyDownEvent || key.RoutedEvent == Keyboard.KeyDownEvent))
            e.Cancel();
    }
}
