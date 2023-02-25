using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для PopupDialog.xaml
    /// </summary>
    public class ValidatePopupDialog
    {
        public bool Result { get; set; }
        public string? Message { get; set; }
        public ValidatePopupDialog(bool result, string? message)
        {
            Result = result;
            Message = message;
        }
    }
    public partial class PopupDialog : UserControl
    {
        private TaskCompletionSource<bool?>? taskCompletion;
        private bool AllowMissClick;
        private static PopupDialog CurrentDialog
        {
            get => Application.Current.Windows.OfType<MainWindow>().First().MainDialog;
        }
        public PopupDialog()=>InitializeComponent();

        #region Options
        private Options CurrentOptions { get; set; } = new Options();
        public class Options
        {
            public bool AllowMissClick { get; set; } = true;
            public DefaultButtonClass DefaultButton { get; set; }=new();
            public class DefaultButtonClass
            {
                public string DefaultButtonText { get; set; } = "ОК";
                public Brush DefaultButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground");
            }
            public SuccessConfirmButtonClass SuccessButton { get; set; }=new();
            public class SuccessConfirmButtonClass
            {
                public string SuccessConfirmButtonText { get; set; } = "Хорошо";
                public Brush SuccessConfirmButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground.Green");
            }
            public CancelConfirmButtonClass CancelButton { get; set; }=new();
            public class CancelConfirmButtonClass
            {
                public string CancelConfirmButtonText { get; set; } = "Отмена";
                public Brush CancelConfirmButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground");
            }
            public SolidColorBrush Background { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#282e33"));
            public string? Title { get; set; } = null;
        }
        #endregion
        #region Properties
        #region TypeDialog
        public enum TypeDialogEnum
        {
            Default,
            Confirm
        }
        public static DependencyProperty TypeDialogProperty = DependencyProperty.Register(nameof(TypeDialog), typeof(TypeDialogEnum), typeof(PopupDialog));
        public TypeDialogEnum TypeDialog
        {
            get
            {
                return (TypeDialogEnum)GetValue(TypeDialogProperty);
            }
            set
            {
                SetValue(TypeDialogProperty, value);
            }
        }
        #endregion
        #region ObjectContent
        public static DependencyProperty ObjectContentProperty = DependencyProperty.Register(nameof(ObjectContent), typeof(object), typeof(PopupDialog));
        public object ObjectContent
        {
            get
            {
                return (object)GetValue(ObjectContentProperty);
            }
            set
            {
                SetValue(ObjectContentProperty, value);
            }
        }
        #endregion

        #region DefaultButton
        public static DependencyProperty DefaultButtonTextProperty = DependencyProperty.Register(nameof(DefaultButtonText), typeof(string), typeof(PopupDialog));
        public string DefaultButtonText
        {
            get
            {
                return (string)GetValue(DefaultButtonTextProperty);
            }
            set
            {
                SetValue(DefaultButtonTextProperty, value);
            }
        }
        #endregion
        #region SuccessConfirmButton
        public static DependencyProperty SuccessConfirmButtonTextProperty = DependencyProperty.Register(nameof(SuccessConfirmButtonText), typeof(string), typeof(PopupDialog));
        public string SuccessConfirmButtonText
        {
            get
            {
                return (string)GetValue(SuccessConfirmButtonTextProperty);
            }
            set
            {
                SetValue(SuccessConfirmButtonTextProperty, value);
            }
        }
        #endregion
        #region CancelConfirmButton
        public static DependencyProperty CancelConfirmButtonTextProperty = DependencyProperty.Register(nameof(CancelConfirmButtonText), typeof(string), typeof(PopupDialog));
        public string CancelConfirmButtonText
        {
            get
            {
                return (string)GetValue(CancelConfirmButtonTextProperty);
            }
            set
            {
                SetValue(CancelConfirmButtonTextProperty, value);
            }
        }
        #endregion
        #region BackgroundModal
        public static DependencyProperty BackgroundModalProperty = DependencyProperty.Register(nameof(BackgroundModal), typeof(SolidColorBrush), typeof(PopupDialog));
        public SolidColorBrush BackgroundModal
        {
            get
            {
                return (SolidColorBrush)GetValue(BackgroundModalProperty);
            }
            set
            {
                SetValue(BackgroundModalProperty, value);
            }
        }
        #endregion
        #region Title
        public static DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PopupDialog));
        public string Title
        {
            get
            {
                return (string)GetValue(TitleProperty);
            }
            set
            {
                SetValue(TitleProperty, value);
            }
        }
        #endregion
        #region ValidateMessage
        public static DependencyProperty ValidateMessageProperty = DependencyProperty.Register(nameof(ValidateMessage), typeof(string), typeof(PopupDialog));
        public string ValidateMessage
        {
            get
            {
                return (string)GetValue(ValidateMessageProperty);
            }
            set
            {
                SetValue(ValidateMessageProperty, value);
            }
        }
        #endregion
        #endregion
        private static Func<ValidatePopupDialog>? validateFunc=null;
        public static async Task<bool?> Show(
            object content,
            TypeDialogEnum typeDialog,
            [Optional] Func<Options, Options> options,
            [Optional] Func<ValidatePopupDialog> validation)
        {
            if (options != null)
            {
                var result = options(new Options());
                CurrentDialog.CurrentOptions = result;
            }
            CurrentDialog.taskCompletion = new TaskCompletionSource<bool?>();
            CurrentDialog.AllowMissClick = CurrentDialog.CurrentOptions.AllowMissClick;

            CurrentDialog.SetValue(PopupDialog.TitleProperty, CurrentDialog.CurrentOptions.Title);
            CurrentDialog.SetValue(PopupDialog.BackgroundModalProperty, CurrentDialog.CurrentOptions.Background);
            CurrentDialog.SetValue(PopupDialog.TypeDialogProperty, typeDialog);
            CurrentDialog.SetValue(PopupDialog.ObjectContentProperty, content);
            CurrentDialog.SetValue(PopupDialog.ValidateMessageProperty, null);

            CurrentDialog.SetValue(PopupDialog.DefaultButtonTextProperty, CurrentDialog
                .CurrentOptions
                .DefaultButton
                .DefaultButtonText);
            CurrentDialog.DefaultButton.Foreground = CurrentDialog
                .CurrentOptions
                .DefaultButton
                .DefaultButtonForeground;

            CurrentDialog.SetValue(PopupDialog.SuccessConfirmButtonTextProperty, CurrentDialog
                .CurrentOptions
                .SuccessButton
                .SuccessConfirmButtonText);
            CurrentDialog.ConfirmButton.Foreground = CurrentDialog
                .CurrentOptions
                .SuccessButton
                .SuccessConfirmButtonForeground;

            CurrentDialog.SetValue(PopupDialog.CancelConfirmButtonTextProperty, CurrentDialog
                .CurrentOptions
                .CancelButton
                .CancelConfirmButtonText);
            CurrentDialog.CancelButton.Foreground = CurrentDialog
                .CurrentOptions
                .CancelButton
                .CancelConfirmButtonForeground;

            CurrentDialog.Visibility = Visibility.Visible;
            validateFunc = validation == null ? null : validation;
            return await CurrentDialog.taskCompletion.Task;
        }
        private void Close(bool? ResultDialog)
        {
            if (ResultDialog == true && validateFunc != null)
            {
                var resultValidate = validateFunc.Invoke();
                if (resultValidate.Result == false)
                {
                    CurrentDialog.SetValue(PopupDialog.ValidateMessageProperty, resultValidate.Message);
                    return;
                }
            }
            var da = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200), FillBehavior.Stop)
            {
                EasingFunction = new CubicEase()
                {
                    EasingMode = EasingMode.EaseInOut,
                }
            };
            da.Completed += (q, e) =>
            {
                CurrentDialog.Visibility = Visibility.Collapsed;
                taskCompletion?.TrySetResult(ResultDialog);
            };
            this.BeginAnimation(OpacityProperty, da);
        }
        private void SuccessConfirmEvent(object sender, RoutedEventArgs e)
        {
            Close(true);
        }
        private void CancelConfirmEvent(object sender, RoutedEventArgs e) =>
           Close(false);
        private void DefaultEvent(object sender, RoutedEventArgs e) =>
           Close(null);
        private void ButtonOutsideClickEvent(object sender, MouseButtonEventArgs e) {
            if (AllowMissClick && !border.IsMouseOver) Close(null);
        }
    }
}
