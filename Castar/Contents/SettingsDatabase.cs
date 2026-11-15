using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Microsoft.Win32;
using System;
using System.Data.Common;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents
{
    public partial class SettingsContent
    {
        private const string SqliteFileFilter = "База SQLite (*.sqlite;*.db)|*.sqlite;*.db";

        private void ChooseSqliteFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = SqliteFileFilter,
                CheckFileExists = false,
                DefaultExt = ".sqlite"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                context.Settings.DataBase.SqlitePath = dialog.FileName;
        }

        private async void ImportDatabase_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (context.Settings.Systems.IsEnableTelegramBot || context.Settings.TelegramBot.Status != "Отключено")
            {
                MessageBox.Show(owner, "Отключите бота Telegram и дождитесь статуса «Отключено» перед переносом.",
                    "Перенос данных", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Новый файл SQLite для переноса",
                Filter = SqliteFileFilter,
                DefaultExt = ".sqlite",
                FileName = "castar-import.sqlite",
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(owner) != true)
                return;
            if (File.Exists(dialog.FileName))
            {
                MessageBox.Show(owner, "Выберите имя нового файла SQLite.", "Перенос данных",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var button = (Button)sender;
            button.IsEnabled = false;
            try
            {
                var database = context.Settings.DataBase;
                var source = DatabaseConnections.GetSqlServerConnectionString(database);
                CastarSqliteImporter.ImportResult result;
                owner.IsEnabled = false;
                try
                {
                    result = await CastarSqliteImporter.ImportAsync(source, dialog.FileName);
                }
                finally
                {
                    owner.IsEnabled = true;
                }
                database.SqlitePath = dialog.FileName;
                database.Engine = DatabaseEngine.Sqlite;
                settingsService.Save(context.Settings);
                MessageBox.Show(owner,
                    $"Перенесено пользователей: {result.Users}, покупок: {result.Purchases}, позиций: {result.Items}, доходов: {result.Incomes}.\nПерезапустите Castar, чтобы открыть SQLite.",
                    "Перенос данных", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show(owner,
                    "Проверьте подключение к SQL Server, схему источника и доступ на запись в папку назначения.",
                    "Перенос данных", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
}
