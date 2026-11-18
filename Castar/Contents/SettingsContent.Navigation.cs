using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Castar.Compontents;

namespace Castar.Contents;

public partial class SettingsContent
{
    private Grid? activeSection;
    private Button? activeSectionButton;
    private Button? nestedPageButton;
    private Func<ValidatePopupDialog>? applyNestedSettings;
    private Action? closeNestedSettings;
    internal event EventHandler? CloseRequested;

    internal void FocusFirstSection() => FirstSettingsSection.Focus();

    private void ClientUpdateBanner_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border banner)
            return;

        banner.BeginAnimation(OpacityProperty, null);
        banner.RenderTransform = Transform.Identity;
        if (e.NewValue is not true || !SystemParameters.ClientAreaAnimation)
            return;

        var offset = new TranslateTransform();
        banner.RenderTransform = offset;
        DoubleAnimation Enter(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        banner.BeginAnimation(OpacityProperty, Enter(0, 1));
        offset.BeginAnimation(TranslateTransform.YProperty, Enter(-6, 0));
    }

    private void OpenSettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StackPanel { Parent: Grid page }, Content: string title } button)
            return;

        activeSection = page;
        activeSectionButton = button;
        SectionMenu.Visibility = Visibility.Collapsed;
        SettingsTitle.Text = title;
        BackToSections.Visibility = Visibility.Visible;
        page.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
        BackToSections.Focus();
    }

    private void BackToSections_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCloseNestedPage())
            BackToSectionMenu();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OpenNestedSettings(Button button, string title, FrameworkElement editor, Func<bool> isCurrent,
        Func<ValidatePopupDialog> validate, Action apply, Action? onClose = null)
    {
        if (activeSection is null || nestedPageButton is not null)
            return;

        nestedPageButton = button;
        closeNestedSettings = onClose;
        applyNestedSettings = () =>
        {
            if (!isCurrent())
                return new(false, "Настройки изменились. Откройте раздел заново.");
            var result = validate();
            if (result.Result)
                apply();
            return result;
        };
        activeSection.Visibility = Visibility.Collapsed;
        SettingsTitle.Text = title;
        NestedSettingsStatus.Text = "";
        NestedSettingsBody.Content = editor;
        NestedSettingsPage.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
        BackToSections.Focus();
    }

    private void ApplyNestedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (applyNestedSettings?.Invoke() is not { } outcome)
            return;
        if (outcome.Result)
            TryCloseNestedPage();
        else
            NestedSettingsStatus.Text = outcome.Message ?? "";
    }

    internal bool TryCloseNestedPage(bool returnToSection = true)
    {
        if (nestedPageButton is not { } button)
            return false;

        nestedPageButton = null;
        applyNestedSettings = null;
        closeNestedSettings?.Invoke();
        closeNestedSettings = null;
        NestedSettingsPage.Visibility = Visibility.Collapsed;
        NestedSettingsBody.Content = null;
        NestedSettingsStatus.Text = "";
        if (returnToSection && activeSection is { } page)
        {
            SettingsTitle.Text = (string)activeSectionButton!.Content;
            page.Visibility = Visibility.Visible;
            SettingsScroll.ScrollToTop();
            (button.IsVisible ? button : BackToSections).Focus();
        }
        return true;
    }

    private void BackToSectionMenu()
    {
        TryCloseNestedPage(returnToSection: false);
        if (activeSection is { } page)
        {
            activeSection = null;
            page.Visibility = Visibility.Collapsed;
        }
        BackToSections.Visibility = Visibility.Collapsed;
        SettingsTitle.Text = "Настройки";
        SectionMenu.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
        if (activeSectionButton?.IsVisible == true)
            activeSectionButton.Focus();
        activeSectionButton = null;
    }
}
