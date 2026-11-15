using Castar.BL;
using Castar.BL.Settings;
using Castar.Compontents;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Castar.Updates;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
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
        private sealed record ReceiptProviderOption(string Id, string Name);

        private class SettingsModelContext: INotifyPropertyChanged
        {
            public required IReadOnlyList<ReceiptProviderOption> ReceiptProviders { get; init; }

            private string updateStatus = "";
            public string UpdateStatus
            {
                get => updateStatus;
                set
                {
                    updateStatus = value;
                    OnPropertyChanged(nameof(UpdateStatus));
                }
            }

            private SettingsModel settings;
            public SettingsModel Settings
            {
                get { return settings; }
                set
                {
                    settings = value;
                    OnPropertyChanged(nameof(Settings));
                }
            }
            private ObservableCollection<User> users;
            public ObservableCollection<User> Users
            {
                get { return users; }
                set
                {
                    users = value;
                    OnPropertyChanged(nameof(Users));
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
        public SettingsContent(
            ISettingsService settingsService, IUsersService usersService, IEnumerable<IReceiptProvider> receiptProviders,
            GitHubUpdateService updateService)
        {
            this.settingsService = settingsService;
            this.usersService = usersService;
            this.updateService = updateService;
            this.DataContext = new SettingsModelContext
            {
                ReceiptProviders = receiptProviders.Select(provider => new ReceiptProviderOption(provider.Id,
                    provider.Id switch
                    {
                        "serbia" => "Сербия — налоговая служба",
                        "nalog" => "Россия — ФНС",
                        _ => provider.Id
                    })).ToArray()
            };
            InitializeComponent();
            context.UpdateStatus = $"Установлена версия {updateService.InstalledVersion}";
            InitializeSettings();
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
        private async Task LoadUsers()
        {
            try
            {
                context.Users = await usersService.GetEntities();
            }
            catch (Exception error) when (error is DbException or ArgumentException or NotSupportedException)
            {
                var message = context.Settings.DataBase.Engine == DatabaseEngine.Sqlite
                    ? "Не удалось прочитать SQLite. Проверьте путь к базе данных и перезапустите Castar."
                    : "Проверьте подключение к SQL Server и выполните DataBase/UpgradeCastar.sql для базы Castar.";
                await PopupDialog.Show(message,
                    PopupDialog.TypeDialogEnum.Default);
            }
        }

        private void DataBasePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (context.Settings != null)
            {
                context.Settings.DataBase.Password = ((PasswordBox)sender).Password;
            }
        }

        private void TelegramToken_Loaded(object sender, RoutedEventArgs e)
        {
            ((PasswordBox)sender).Password = context.Settings.TelegramBot.TelegramBotApi_KEY ?? "";
        }

        private void TelegramToken_LostFocus(object sender, RoutedEventArgs e)
        {
            context.Settings.TelegramBot.TelegramBotApi_KEY = ((PasswordBox)sender).Password.Trim();
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
            if (context.Settings?.DataBase != null)
            {
                ((PasswordBox)sender).Password = context.Settings.DataBase.Password;
            }
        }

        private void NalogFNSPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (context.Settings != null)
            {
                context.Settings.NalogFNS.Password = ((PasswordBox)sender).Password;
            }
        }

        private void NalogFNSPasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (context.Settings?.NalogFNS != null)
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
                    await usersService.Update(updatedUser);
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
                await usersService.Add(createdUser);
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

        private async void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            if (context.Users == null)
                await LoadUsers();
        }
    }
}
