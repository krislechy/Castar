using Castar.BL;
using Castar.BL.Settings;
using Castar.Commands;
using Castar.Compontents;
using Castar.Contents;
using Castar.Externs;
using Castar.TrayNotify.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace Castar
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SettingsContent settingsContent;
        private readonly IncomesContent incomesContent;
        private readonly ISettingsService settingsService;
        public MainWindow(SettingsContent settingsContent,ISettingsService settingsService,IncomesContent incomesContent)
        {
            this.settingsContent = settingsContent;
            this.settingsService = settingsService;
            this.incomesContent = incomesContent;
            InitializeComponent();
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
        private void SystemButtonClose(object sender, RoutedEventArgs e) => TryClose();

        private void thisClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            TryClose();
        }
        private async void TryClose()
        {
            if (settingsService.Get().Systems.IsHideInTray)
            {
                this.Hide();
            }
            else
            {
                var result = await PopupDialog.Show(
                    "Закрыть приложение?", 
                    PopupDialog.TypeDialogEnum.Confirm,
                    (options) =>
                    {
                        options.SuccessButton.SuccessConfirmButtonText = "Закрыть Castar";
                        options.SuccessButton.SuccessConfirmButtonForeground = System.Windows.Media.Brushes.OrangeRed;
                        return options;
                    }) == true ? true : false;
                if (result)
                {
                    settingsService.Dispose();
                    Environment.Exit(0);
                }
            }
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
        private void MenuSelectorCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var result= ResetMenuToggleButtons((ToggleButton)e.Source);
            if (!result) return;

            var content = e.Parameter;
            if (content != null)
            {
                switch(content)
                {
                    case ("Purchases"):
                        {
                            Trace.WriteLine("purhcases");
                            break;
                        }
                    case ("Incomes"):
                        {
                            Trace.WriteLine("incomes");
                            MainContent.Content = incomesContent;
                            break;
                        }
                    case ("Settings"):
                        {
                            Trace.WriteLine("settings");
                            MainContent.Content = settingsContent;
                            break;
                        }
                }
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
