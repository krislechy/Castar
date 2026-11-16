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

        private void ChooseImageButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFileDialog();
            folderDialog.Filter = "Изображение (*.png;*.jpg)|*.png;*.jpg";
            var result = folderDialog.ShowDialog(Window.GetWindow(this));
            if (result.HasValue && result.Value == true)
            {
                try
                {
                    var file = new FileInfo(folderDialog.FileName);
                    if (file.Length > 1024 * 1024)
                    {
                        PopupDialog.SetContentWarning(this, "Выберите изображение размером до 1 МБ.");
                        return;
                    }
                    var user = (User)DataContext;
                    user.ImageRaw = File.ReadAllBytes(file.FullName);
                    DataContext = null;
                    DataContext = user;
                    PopupDialog.SetContentWarning(this, null);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    PopupDialog.SetContentWarning(this, "Не удалось открыть изображение.");
                }
            }
        }
    }
}
