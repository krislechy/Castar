using Castar.Compontents;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents;

public partial class SettingsContent
{
    private Grid? activeSection;
    private Button? activeSectionButton;

    private void OpenSettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IslandPanel panel } button || panel.Parent is not Grid page)
            return;

        activeSection = page;
        activeSectionButton = button;
        SectionMenu.Visibility = Visibility.Collapsed;
        SettingsTitle.Title = panel.Title;
        BackToSections.Visibility = Visibility.Visible;
        page.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
        BackToSections.Focus();
    }

    private void BackToSections_Click(object sender, RoutedEventArgs e) => BackToSectionMenu();

    private void SettingsSection_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is IslandPanel { Visibility: not Visibility.Visible, Parent: Grid page } && page == activeSection)
            BackToSectionMenu();
    }

    private void BackToSectionMenu()
    {
        if (activeSection is { } page)
        {
            activeSection = null;
            page.Visibility = Visibility.Collapsed;
        }
        BackToSections.Visibility = Visibility.Collapsed;
        SettingsTitle.Title = "Настройки";
        SectionMenu.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
        if (activeSectionButton?.IsVisible == true)
            activeSectionButton.Focus();
        activeSectionButton = null;
    }
}
