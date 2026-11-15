using Castar.Updates;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents
{
    public partial class SettingsContent
    {
        private readonly GitHubUpdateService updateService;

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            context.UpdateStatus = "Проверка обновлений…";
            try
            {
                var update = await updateService.CheckAsync();
                if (update is null)
                {
                    context.UpdateStatus = $"Версия {updateService.InstalledVersion} актуальна.";
                    return;
                }

                context.UpdateStatus = $"Доступна версия {update.Version}.";
                var result = MessageBox.Show(Window.GetWindow(this),
                    $"Скачать и запустить установщик Castar {update.Version}? Castar закроется перед установкой.",
                    "Обновление Castar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                    return;

                context.UpdateStatus = $"Загрузка версии {update.Version}…";
                var installer = await updateService.DownloadAsync(update);
                using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                if (process is null)
                    throw new InvalidOperationException("Не удалось запустить установщик Castar.");
                ((MainWindow)Application.Current.MainWindow).Exit();
            }
            catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
                or OperationCanceledException or Win32Exception or InvalidOperationException)
            {
                context.UpdateStatus = "Не удалось проверить или установить обновление. Повторите попытку.";
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
}
