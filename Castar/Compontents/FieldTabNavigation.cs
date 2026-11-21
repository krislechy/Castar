using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Castar.Compontents;

internal static class FieldTabNavigation
{
    [ThreadStatic]
    private static bool installed;

    internal static void Install()
    {
        if (installed)
            return;
        installed = true;
        InputManager.Current.PreProcessInput += HandleTab;
    }

    private static void HandleTab(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not KeyEventArgs { Key: Key.Tab } key
            || key.RoutedEvent != Keyboard.PreviewKeyDownEvent
            || Application.Current?.MainWindow is not MainWindow { IsVisible: true } window)
            return;

        e.Cancel();
        var modifiers = key.KeyboardDevice.Modifiers;
        if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
            MoveFocus(window.FieldNavigationScope, backwards: modifiers == ModifierKeys.Shift);
    }

    internal static bool MoveFocus(UIElement scope, bool backwards, bool fromStart = false)
    {
        var fields = new List<Control>();
        CollectFields(scope, fields);
        if (fields.Count == 0)
            return false;

        var current = fromStart ? -1 : fields.FindIndex(field => field.IsKeyboardFocusWithin);
        var direction = backwards ? -1 : 1;
        var next = current < 0 ? (backwards ? fields.Count - 1 : 0) : current + direction;
        for (var attempt = 0; attempt < fields.Count; attempt++, next += direction)
        {
            var field = fields[(next + fields.Count) % fields.Count];
            if (!field.Focus())
                continue;

            field.BringIntoView();
            return true;
        }
        return false;
    }

    private static void CollectFields(DependencyObject element, List<Control> fields)
    {
        if (element is UIElement { IsVisible: false } or UIElement { IsEnabled: false })
            return;
        if (element is BudgetUserPicker)
            return;

        if (element is TextBoxBase or PasswordBox or ComboBox)
        {
            if (element is Control { Focusable: true, IsTabStop: true } field
                && field is not TextBoxBase { IsReadOnly: true }
                && field is not ComboBox { IsEditable: true, IsReadOnly: true })
                fields.Add(field);
            return;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            CollectFields(VisualTreeHelper.GetChild(element, index), fields);
    }
}
