using Castar.BL;
using Castar.BL.Settings;
using Castar.Compontents;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security;
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

namespace Castar.Contents
{
    /// <summary>
    /// Логика взаимодействия для SettingsContent.xaml
    /// </summary>
    public partial class SettingsContent : UserControl
    {
        private class SettingsModelContext: INotifyPropertyChanged
        {
            private SettingsModel settings;
            public SettingsModel Settings
            {
                get { return settings; }
                set
                {
                    settings = value;
                    OnPropertyChanged(nameof(settings));
                }
            }
            private ObservableCollection<User> users;
            public ObservableCollection<User> Users
            {
                get { return users; }
                set
                {
                    users = value;
                    OnPropertyChanged(nameof(users));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private SettingsModelContext context
        {
            get
            {
                return (SettingsModelContext)this.DataContext;
            }
        }
        private readonly ISettingsService settingsService;
        private readonly IUsersService usersService;
        public SettingsContent(ISettingsService settingsService, IUsersService usersService)
        {
            this.settingsService = settingsService;
            this.usersService = usersService;
            this.DataContext = new SettingsModelContext();
            InitializeComponent();
        }
        private void InitializeSettings()
        {
            var obj = settingsService.Get();
            obj.SetHandlersEvents(() =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    settingsService.Save(context.Settings);
                    settingsService.Apply();
                });
            });
            context.Settings = obj;
        }
        private void LoadUsers()
        {
            Dispatcher.BeginInvoke(async () =>
            {
                var users = await this.usersService.GetEntities();
                context.Users = users;
            });
        }

        private void DataBasePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (this.DataContext != null)
            {
                context.Settings.DataBase.Password = ((PasswordBox)sender).Password;
            }
        }
        private void OpenSelectionFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFileDialog();
            folderDialog.Filter = "Файл базы данных (*.mdf)|*.mdf";
            var result = folderDialog.ShowDialog(Application.Current.MainWindow);
            if (result.HasValue && result.Value == true)
                context.Settings.DataBase.AttachDBFileName = folderDialog.FileName;
        }
        private void DataBasePasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.DataContext != null && context.Settings.DataBase != null)
            {
                ((PasswordBox)sender).Password = context.Settings.DataBase.Password;
            }
        }

        private void NalogFNSPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (this.DataContext != null)
            {
                context.Settings.NalogFNS.Password = ((PasswordBox)sender).Password;
            }
        }

        private void NalogFNSPasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.DataContext != null && context.Settings.NalogFNS != null)
            {
                ((PasswordBox)sender).Password = context.Settings.NalogFNS.Password;
            }
        }

        private async void UsersListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var obj = (ListView)sender;
            if (obj.SelectedValue != null)
            {
                var selected = (User)obj.SelectedValue;
                var userInfo = new UserInfoPanel();
                userInfo.IsEditContent = true;
                userInfo.SizeImage = 100;
                userInfo.DataContext = selected.Clone<User>();
                var result = await PopupDialog.Show(userInfo, PopupDialog.TypeDialogEnum.Confirm, (o) =>
                {
                    o.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                    o.SuccessButton.SuccessConfirmButtonText = "Сохранить";
                    o.AllowMissClick = false;
                    o.Title = "Редактирование пользователя";
                    return o;
                });
                if (result != null && result.Value)
                {
                    var updatedUser = (User)userInfo.DataContext;
                    if (updatedUser.Name == null) updatedUser.Name = "NONAME";
                    usersService.Update(updatedUser);
                    var source = (ObservableCollection<User>)obj.ItemsSource;
                    for (int i = 0; i < source.Count(); i++)
                    {
                        if (source[i].Id == updatedUser.Id)
                        {
                            source[i] = updatedUser;
                            break;
                        }
                    }
                }
            }
        }

        private async void NewUserButton_Click(object sender, RoutedEventArgs e)
        {
            var userInfo = new UserInfoPanel();
            userInfo.IsEditContent = true;
            userInfo.SizeImage = 100;
            userInfo.DataContext = new User();
            var result = await PopupDialog.Show(userInfo, PopupDialog.TypeDialogEnum.Confirm, (o) =>
            {
                o.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                o.SuccessButton.SuccessConfirmButtonText = "Создать";
                o.AllowMissClick = true;
                o.Title = "Создание пользователя";
                return o;
            });
            if (result != null && result.Value)
            {
                var createdUser = (User)userInfo.DataContext;
                if (createdUser.Name == null) createdUser.Name = "NONAME";
                usersService.Add(createdUser);
                context.Users?.Add(createdUser);
            }
        }

        private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var contextMenu = (ContextMenu)menuItem.Parent;
            var listView = (ListView)contextMenu.PlacementTarget;
            var selectedValue = (User)listView.SelectedValue;
            if (selectedValue != null)
            {
                var result = await PopupDialog.Show($"Вы уверены, что хотите удалить пользователя:\n{selectedValue.Name} - Telegram ID: {selectedValue.TelegramId} ?",
                    PopupDialog.TypeDialogEnum.Confirm, (o) =>
                {
                    o.SuccessButton.SuccessConfirmButtonText = "Удалить";
                    o.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                    o.AllowMissClick = true;
                    return o;
                });
                if (result != null && result.Value)
                {
                    var resultDeleted = await usersService.Delete(selectedValue.Id);
                    if (resultDeleted)
                    {
                        context.Users.Remove(selectedValue);
                    }
                    else
                    {
                        await PopupDialog.Show("Не удалось удалить пользователя", PopupDialog.TypeDialogEnum.Default);
                    }
                }
            }
        }

        private void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            if (context.Settings == null)
                InitializeSettings();
            if (context.Users == null)
                LoadUsers();
        }
    }
}
