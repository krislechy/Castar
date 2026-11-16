using Castar.Settings;
using Castar.Api;
using Castar.Commands;
using Castar.Contents;
using Castar.Externs;
using Castar.TrayNotify.Windows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Castar
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SettingsContent settingsContent;
        private readonly IncomesContent incomesContent;
        private readonly PurchasesContent purchasesContent;
        private readonly Lazy<StatisticsContent>? statisticsContent;
        private readonly ClientSettings settings;
        private (string Kind, Guid? Id, DateTime? AddedOn)? notificationTarget;
        private IInputElement? focusBeforeSettings;
        public MainWindow(SettingsContent settingsContent, ClientSettings settings, IncomesContent incomesContent,
            PurchasesContent purchasesContent, Lazy<StatisticsContent>? statisticsContent = null)
        {
            this.settingsContent = settingsContent;
            this.settings = settings;
            this.incomesContent = incomesContent;
            this.purchasesContent = purchasesContent;
            this.statisticsContent = statisticsContent;
            InitializeComponent();
            StatisticsMenuButton.Visibility = statisticsContent is null ? Visibility.Collapsed : Visibility.Visible;
            MainDialog.IsVisibleChanged += (_, _) =>
            {
                if (!MainDialog.IsVisible && IsVisible && notificationTarget is { } target)
                {
                    notificationTarget = null;
                    NavigateToBudgetEvent(target.Kind, target.Id, target.AddedOn);
                }
            };
            settings.ConnectionChanged += (_, _) => notificationTarget = null;
            if (settings.ApiKey.Length == 0)
                ShowSettings();
        }

        #region TopControls
        private void Grid_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    double percentHorizontal = e.GetPosition(this).X / ActualWidth;
                    double targetHorizontal = RestoreBounds.Width * percentHorizontal;

                    double percentVertical = e.GetPosition(this).Y / ActualHeight;
                    double targetVertical = RestoreBounds.Height * percentVertical;

                    WindowState = WindowState.Normal;

                    var scale = ScalingDpi.GetScalingFactor();
                    var cursorPos = CursorPosition.GetCursorPosition();

                    var difX = cursorPos.X / scale - targetHorizontal;
                    Left = difX;

                    var difY = cursorPos.Y / scale - targetVertical;
                    Top = difY;
                }
                try
                {
                    DragMove();
                }
                catch { }
            }
        }

        private void SystemCommandBinding_Executed_Minimize(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void SystemCommandBinding_Executed_Maximize(object sender, ExecutedRoutedEventArgs e) => ReStateWindow();
        private void ReStateWindow()
        {
            if (WindowState == WindowState.Normal)
                SystemCommands.MaximizeWindow(this);
            else
                SystemCommands.RestoreWindow(this);
        }
        private void SystemCommandBinding_CanExecute_Default(object sender, CanExecuteRoutedEventArgs e)=> e.CanExecute = true;
        private void SystemButtonClose(object sender, RoutedEventArgs e) => Close();

        private void thisClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (settings.IsHideInTray)
            {
                e.Cancel = true;
                Hide();
            }
        }

        internal void Exit()
        {
            Closing -= thisClosing;
            Application.Current.Shutdown();
        }
        private bool IsBusyRestateWindow = false;
        private async void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && !IsBusyRestateWindow) { 
                IsBusyRestateWindow = true;
                await Task.Delay(150); 
                ReStateWindow(); 
                IsBusyRestateWindow = false; 
            }
        }
        #endregion

        #region MenuToggleButtons & Commands
        internal void ShowBudgetNotification(string kind, Guid? entityId, DateTime? addedOn)
        {
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();

            if (MainDialog.IsVisible)
                notificationTarget = (kind, entityId, addedOn);
            else
                NavigateToBudgetEvent(kind, entityId, addedOn);
        }

        private void NavigateToBudgetEvent(string kind, Guid? entityId, DateTime? addedOn)
        {
            var section = kind switch
            {
                BudgetEvents.Purchase => "Purchases",
                BudgetEvents.Income => "Incomes",
                _ => null
            };
            if (section is null)
                return;

            CloseSettings(restoreFocus: false);
            var button = MenuToggleButtons.Children.OfType<ToggleButton>()
                .First(toggle => Equals(toggle.CommandParameter, section));
            button.IsChecked = true;
            ResetMenuToggleButtons(button);
            SelectSection(section);
            if (entityId is { } id)
            {
                if (kind == BudgetEvents.Purchase)
                    purchasesContent.RevealNotification(id, addedOn);
                else
                    incomesContent.RevealNotification(id, addedOn);
            }
        }

        private void MenuSelectorCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is "Settings")
            {
                if (SettingsOverlay.Visibility == Visibility.Visible)
                    CloseSettings();
                else
                    ShowSettings();
                return;
            }

            if (ResetMenuToggleButtons((ToggleButton)e.Source) && e.Parameter is string section)
                SelectSection(section);
        }

        private void ShowSettings()
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
                return;

            focusBeforeSettings = Keyboard.FocusedElement;
            SettingsPresenter.Content = settingsContent;
            SettingsMenuButton.IsChecked = true;
            SettingsOverlay.Visibility = Visibility.Visible;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (SettingsOverlay.IsVisible && !MainDialog.IsVisible)
                    SettingsOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, DispatcherPriority.Input);
        }

        private void CloseSettings(bool restoreFocus = true)
        {
            if (SettingsOverlay.Visibility != Visibility.Visible)
                return;

            SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsPresenter.Content = null;
            SettingsMenuButton.IsChecked = false;
            var previous = focusBeforeSettings;
            focusBeforeSettings = null;
            if (restoreFocus)
                Keyboard.Focus(previous is UIElement { IsVisible: true, IsEnabled: true } element && IsAncestorOf(element)
                    ? element : SettingsMenuButton);
        }

        private void SettingsBackdrop_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseSettings();
        }

        private void SettingsOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || MainDialog.IsVisible)
                return;

            e.Handled = true;
            CloseSettings();
        }

        private void SelectSection(string section)
        {
            switch (section)
            {
                case "Purchases":
                    MainContent.Content = purchasesContent;
                    break;
                case "Incomes":
                    MainContent.Content = incomesContent;
                    break;
                case "Statistics" when statisticsContent is not null:
                    MainContent.Content = statisticsContent.Value;
                    break;
            }
        }

        private bool ResetMenuToggleButtons(ToggleButton currentToggleButton)
        {
            if (currentToggleButton.IsChecked == false) { currentToggleButton.IsChecked = true; return false; };
            var toggleButtons = MenuToggleButtons.Children.OfType<ToggleButton>();
            foreach (var toggleButton in toggleButtons)
                if (!currentToggleButton.Equals(toggleButton))
                    toggleButton.IsChecked = false;
            return true;
        }
        #endregion
    }
}
