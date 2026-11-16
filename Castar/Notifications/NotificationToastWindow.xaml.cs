using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Notifications;

internal partial class NotificationToastWindow : Window
{
    internal const int PreferredWidth = 332;
    internal const int CardSlotHeight = 62;
    internal const int FooterHeight = 28;

    internal NotificationToastWindow() => InitializeComponent();

    internal event Action<CastarNotificationCenter.Notice>? OpenRequested;
    internal event Action<CastarNotificationCenter.Notice>? DismissRequested;
    internal event Action? DismissAllRequested;

    internal void ShowNotices(IReadOnlyList<CastarNotificationCenter.Notice> cards, bool canDismissAll)
    {
        NotificationItems.ItemsSource = cards;
        DismissAllPanel.Visibility = canDismissAll ? Visibility.Visible : Visibility.Collapsed;
        if (cards.Count > 0)
            Height = cards.Count * CardSlotHeight + (canDismissAll ? FooterHeight : 0);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CastarNotificationCenter.Notice notice })
            OpenRequested?.Invoke(notice);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CastarNotificationCenter.Notice notice })
            DismissRequested?.Invoke(notice);
    }

    private void DismissAll_Click(object sender, RoutedEventArgs e) => DismissAllRequested?.Invoke();
}
