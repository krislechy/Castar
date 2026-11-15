using Castar.Updates;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
            var showingProgress = false;
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
                    $"Установить версию {update.Version}? Castar перезапустится.",
                    "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                    return;

                showingProgress = true;
                context.IsDownloadingUpdate = true;
                context.UpdateDownloadPercent = 0;
                context.UpdateStatus = $"Загрузка версии {update.Version}…";
                var progress = new Progress<long>(downloaded =>
                {
                    if (!showingProgress)
                        return;
                    var percent = downloaded * 100 / update.SizeBytes;
                    context.UpdateDownloadPercent = (int)percent;
                    context.UpdateStatus = $"Загрузка {update.Version} — {downloaded / 1048576d:F1} из {update.SizeBytes / 1048576d:F1} МБ ({percent} %)";
                });
                var archive = await updateService.DownloadAsync(update, progress: progress);
                showingProgress = false;
                context.IsDownloadingUpdate = false;
                context.UpdateStatus = "Подготовка обновления…";
                var payload = Path.Combine(Path.GetDirectoryName(archive)!, "payload");
                await Task.Run(() =>
                {
                    using var package = ZipFile.OpenRead(archive);
                    const long maxExpandedBytes = 512L * 1024 * 1024;
                    long size = 0;
                    foreach (var file in package.Entries)
                    {
                        if (file.Length > maxExpandedBytes - size)
                            throw new InvalidDataException("Распакованный архив обновления слишком большой.");
                        size += file.Length;
                    }
                    package.ExtractToDirectory(payload);
                });
                var helper = new ProcessStartInfo(Path.Combine(payload, "Castar.Updater.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = payload
                };
                helper.ArgumentList.Add(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
                helper.ArgumentList.Add(payload);
                helper.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var process = Process.Start(helper);
                if (process is null)
                    throw new InvalidOperationException("Не удалось применить обновление Castar.");
                ((MainWindow)Application.Current.MainWindow).Exit();
            }
            catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
                or OperationCanceledException or Win32Exception or InvalidOperationException)
            {
                context.UpdateStatus = "Не удалось обновить Castar. Повторите попытку.";
            }
            finally
            {
                showingProgress = false;
                context.IsDownloadingUpdate = false;
                button.IsEnabled = true;
            }
        }
    }
}
