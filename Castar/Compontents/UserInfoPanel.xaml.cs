using Castar.Domain.Models.DataBase;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для UserInfoPanel.xaml
    /// </summary>
    public partial class UserInfoPanel : UserControl
    {
        public UserInfoPanel()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty CanSetTelegramNotificationsProperty =
            DependencyProperty.Register(nameof(CanSetTelegramNotifications), typeof(bool), typeof(UserInfoPanel));

        public bool CanSetTelegramNotifications
        {
            get => (bool)GetValue(CanSetTelegramNotificationsProperty);
            set => SetValue(CanSetTelegramNotificationsProperty, value);
        }

        public static DependencyProperty SizeImageProperty = DependencyProperty.Register(nameof(SizeImage), typeof(double), typeof(UserInfoPanel));
        public double SizeImage
        {
            get
            {
                return (double)GetValue(SizeImageProperty);
            }
            set
            {
                SetValue(SizeImageProperty, value);
            }
        }
        public static DependencyProperty IsEditContentProperty = DependencyProperty.Register(nameof(IsEditContent), typeof(bool), typeof(UserInfoPanel));
        public bool IsEditContent
        {
            get
            {
                return (bool)GetValue(IsEditContentProperty);
            }
            set
            {
                SetValue(IsEditContentProperty, value);
            }
        }
        public static DependencyProperty IsShortViewContentProperty = DependencyProperty.Register(nameof(IsShortView), typeof(bool), typeof(UserInfoPanel));
        public bool IsShortView
        {
            get
            {
                return (bool)GetValue(IsShortViewContentProperty);
            }
            set
            {
                SetValue(IsShortViewContentProperty, value);
            }
        }

        private void SexRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            var rb = (RadioButton)sender;
            if (rb.IsChecked != null)
            {
                if (rb.IsChecked == true)
                {
                    var value = int.Parse((string)rb.Tag);
                    var user = (User)DataContext;
                    user.Sex = value;
                }
            }
        }

        private void SexRadioButton_Loaded(object sender, RoutedEventArgs e)
        {
            var rb = (RadioButton)sender;
            var user = (User)DataContext;
            if (rb.Tag != null && user!=null)
            {
                var value = int.Parse((string)rb.Tag);
                if (value == user.Sex)
                    rb.IsChecked = true;
            }
        }

        private async void ChooseImageButton_Click(object sender, RoutedEventArgs e)
        {
            var chooser = new OpenFileDialog { Filter = "Изображения (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
            if (chooser.ShowDialog(Window.GetWindow(this)) != true || DataContext is not User user)
                return;

            try
            {
                var image = await Task.Run(() => UserAvatarEditor.ReadImage(chooser.FileName));
                if (!IsVisible || !ReferenceEquals(DataContext, user))
                    return;

                var editor = new UserAvatarEditor(image);
                var accepted = await PopupDialog.ShowNested(this, editor, options =>
                {
                    options.Title = "Аватар";
                    options.AllowMissClick = false;
                    options.SuccessButton.SuccessConfirmButtonText = "Применить";
                    return options;
                }, editor.Validate);

                if (accepted == true && editor.Result is { } cropped && ReferenceEquals(DataContext, user))
                {
                    user.ImageRaw = cropped;
                    DataContext = null;
                    DataContext = user;
                    PopupDialog.SetContentWarning(this, null);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                or NotSupportedException or ArgumentException)
            {
                PopupDialog.SetContentWarning(this, "Не удалось открыть изображение.");
            }
        }
    }
}
