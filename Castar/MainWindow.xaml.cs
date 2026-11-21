using Castar.Settings;
using Castar.Api;
using Castar.Commands;
using Castar.Contents;
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
using System.Windows.Media.Animation;
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
        private readonly CategoriesContent categoriesContent;
        private readonly Lazy<StatisticsContent>? statisticsContent;
        private readonly ClientSettings settings;
        private bool statisticsNavigationAttached;
        private (string Kind, Guid? Id, DateTime? AddedOn)? notificationTarget;
        private IInputElement? focusBeforeSettings;
        private const double CollapsedNavigationWidth = 64;
        private const double ExpandedNavigationWidth = 240;
        private bool skipNavigationAnimation;

        internal UIElement FieldNavigationScope => MainDialog is { IsVisible: true, ActiveDialog: not null }
            ? MainDialog : SettingsOverlay.IsVisible ? SettingsOverlay : this;

        public static readonly DependencyProperty IsNavigationExpandedProperty = DependencyProperty.Register(
            nameof(IsNavigationExpanded), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, (owner, _) => ((MainWindow)owner).UpdateNavigationState()));

        public bool IsNavigationExpanded
        {
            get => (bool)GetValue(IsNavigationExpandedProperty);
            set => SetValue(IsNavigationExpandedProperty, value);
        }
        public MainWindow(SettingsContent settingsContent, ClientSettings settings, IncomesContent incomesContent,
            PurchasesContent purchasesContent, CategoriesContent categoriesContent, Lazy<StatisticsContent>? statisticsContent = null)
        {
            this.settingsContent = settingsContent;
            this.settings = settings;
            this.incomesContent = incomesContent;
            this.purchasesContent = purchasesContent;
            this.categoriesContent = categoriesContent;
            this.statisticsContent = statisticsContent;
            InitializeComponent();
            StateChanged += (_, _) => UpdateMaximizedBorder(VisualTreeHelper.GetDpi(this));
            DpiChanged += (_, e) => UpdateMaximizedBorder(e.NewDpi);
            SharedPeriodSelector.DisplayTimeZone = () => settings.DisplayTimeZone;
            SharedPeriodSelector.SetCurrentMonth();
            incomesContent.BindPeriodSelector(SharedPeriodSelector);
            purchasesContent.BindPeriodSelector(SharedPeriodSelector);
            SettingsPresenter.Content = settingsContent;
            settingsContent.CloseRequested += (_, _) => CloseSettings();
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
        private const int HorizontalSizingFrame = 32;
        private const int VerticalSizingFrame = 33;
        private const int PaddedBorder = 92;

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetricsForDpi(int metric, uint dpi);

        private void UpdateMaximizedBorder(DpiScale scale)
        {
            if (WindowState != WindowState.Maximized)
            {
                ClearValue(BorderThicknessProperty);
                return;
            }

            // Windows maximizes the outer resize frame beyond the monitor work area.
            var dpi = (uint)Math.Round(scale.PixelsPerInchX);
            var padding = GetSystemMetricsForDpi(PaddedBorder, dpi);
            var horizontal = (GetSystemMetricsForDpi(HorizontalSizingFrame, dpi) + padding) / scale.DpiScaleX;
            var vertical = (GetSystemMetricsForDpi(VerticalSizingFrame, dpi) + padding) / scale.DpiScaleY;
            BorderThickness = new Thickness(horizontal, vertical, horizontal, vertical);
        }

        private void MinimizeWindow_Executed(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void MaximizeWindow_Executed(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
        private void RestoreWindow_Executed(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
        private void CloseWindow_Executed(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

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
            // Detach the previous page before changing the shared range, so it cannot
            // request data for the target page's period while navigation is in progress.
            MainContent.Content = null;
            if (entityId is { } id)
            {
                if (kind == BudgetEvents.Purchase)
                    purchasesContent.RevealNotification(id, addedOn);
                else
                    incomesContent.RevealNotification(id, addedOn);
            }
            SelectSection(section);
        }

        private void MenuSelectorCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SetNavigationExpanded(false);
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

        internal async Task OpenCategorySettingsAsync(string kind)
        {
            if (MainDialog.ActiveDialog is not null)
                return;

            var title = categoriesContent.SelectKind(kind);
            await Compontents.PopupDialog.Show(categoriesContent, Compontents.PopupDialog.TypeDialogEnum.Default, options =>
            {
                options.Title = title;
                options.EditorIcon = (Geometry)categoriesContent.FindResource("CategoryTagIcon");
                options.DefaultButton.DefaultButtonText = "Закрыть";
                return options;
            });
        }

        private void ShowSettings()
        {
            SetNavigationExpanded(false);
            if (SettingsOverlay.Visibility == Visibility.Visible)
                return;

            focusBeforeSettings = Keyboard.FocusedElement;
            _ = settingsContent.RetryUnavailableDataAsync();
            SettingsMenuButton.IsChecked = true;
            SettingsOverlay.Visibility = Visibility.Visible;
            _ = settingsContent.RefreshClientUpdateAsync();
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (SettingsOverlay.IsVisible && !MainDialog.IsVisible)
                    settingsContent.FocusFirstSection();
            }, DispatcherPriority.Input);
        }

        private void CloseSettings(bool restoreFocus = true)
        {
            if (SettingsOverlay.Visibility != Visibility.Visible)
                return;

            SettingsOverlay.Visibility = Visibility.Collapsed;
            settingsContent.CloseScreen();
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

        private void SettingsPresenter_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var radius = SettingsShell.CornerRadius.TopLeft - SettingsShell.BorderThickness.Left;
            SettingsPresenter.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        }

        private void SettingsOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || MainDialog.IsVisible)
                return;

            e.Handled = true;
            if (!settingsContent.TryCloseNestedPage())
                CloseSettings();
        }

        private void MainWorkspace_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (SettingsOverlay.IsVisible)
                e.Handled = true;
        }

        private void SelectSection(string section)
        {
            SetNavigationExpanded(false);
            var selectedNavigation = MenuToggleButtons.Children.OfType<ToggleButton>()
                .FirstOrDefault(button => Equals(button.CommandParameter, section));
            if (selectedNavigation is not null)
            {
                selectedNavigation.IsChecked = true;
                ResetMenuToggleButtons(selectedNavigation);
            }
            switch (section)
            {
                case "Purchases":
                    MainContent.Content = purchasesContent;
                    break;
                case "Incomes":
                    MainContent.Content = incomesContent;
                    break;
                case "Statistics" when statisticsContent is not null:
                    statisticsContent.Value.BindPeriodSelector(SharedPeriodSelector);
                    if (!statisticsNavigationAttached)
                    {
                        statisticsContent.Value.ExpenseDayRequested += ShowExpenseDay;
                        statisticsNavigationAttached = true;
                    }
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
        private void ShowExpenseDay(DateOnly day)
        {
            MainContent.Content = null;
            purchasesContent.ShowDay(day);
            var button = MenuToggleButtons.Children.OfType<ToggleButton>()
                .First(item => Equals(item.CommandParameter, "Purchases"));
            button.IsChecked = true;
            ResetMenuToggleButtons(button);
            SelectSection("Purchases");
        }

        internal void SetNavigationExpanded(bool expanded, bool animate = true)
        {
            skipNavigationAnimation = !animate;
            try
            {
                if (IsNavigationExpanded == expanded)
                {
                    if (!animate) UpdateNavigationState();
                    return;
                }
                IsNavigationExpanded = expanded;
            }
            finally { skipNavigationAnimation = false; }
        }

        private void NavigationDrawer_MouseEnter(object sender, MouseEventArgs e) =>
            SetNavigationExpanded(true);

        private void NavigationDrawer_MouseLeave(object sender, MouseEventArgs e) =>
            SetNavigationExpanded(false);

        private void NavigationDismiss_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SetNavigationExpanded(false);
        }

        private void Navigation_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !IsNavigationExpanded) return;
            e.Handled = true;
            SetNavigationExpanded(false, animate: false);
            MenuToggleButtons.Children.OfType<ToggleButton>().FirstOrDefault(button => button.IsChecked == true)?.Focus();
        }

        private void NavigationDrawer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var width = IsNavigationExpanded ? ExpandedNavigationWidth : CollapsedNavigationWidth;
            NavigationClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            NavigationClip.Rect = new Rect(0, 0, width, e.NewSize.Height);
        }

        private void UpdateNavigationState()
        {
            if (NavigationDrawer is null || NavigationClip is null) return;
            NavigationDismissLayer.Visibility = IsNavigationExpanded ? Visibility.Visible : Visibility.Collapsed;
            var current = NavigationClip.Rect;
            var target = new Rect(0, 0, IsNavigationExpanded ? ExpandedNavigationWidth : CollapsedNavigationWidth,
                Math.Max(0, NavigationDrawer.ActualHeight));
            NavigationClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            NavigationClip.Rect = target;
            if (skipNavigationAnimation || !IsLoaded || !SystemParameters.ClientAreaAnimation || current == target)
                return;
            // Fixed content rail: animate only the overlay clip, never the workspace layout.
            var animation = new RectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteRectKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineRectKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)),
                new KeySpline(0.32, 0.72, 0, 1)));
            NavigationClip.BeginAnimation(RectangleGeometry.RectProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        #endregion
    }
}
