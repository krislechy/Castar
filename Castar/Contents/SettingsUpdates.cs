using Castar.Compontents;
using Castar.Updates;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace Castar.Contents
{
    public partial class SettingsContent
    {
        private readonly GitHubUpdateService updateService;
        private Task? clientUpdateRefresh;
        private CastarUpdate? availableClientUpdate;
        private int clientUpdateInstallAttempt;

        internal Task RefreshClientUpdateAsync()
        {
            if (context.IsInstallingClientUpdate)
                return Task.CompletedTask;
            if (clientUpdateRefresh is { IsCompleted: false } refresh)
                return refresh;

            return clientUpdateRefresh = CheckClientUpdateAsync(clientUpdateInstallAttempt);
        }

        private async Task CheckClientUpdateAsync(int installAttempt)
        {
            CastarUpdate? update;
            try
            {
                update = await updateService.CheckAsync();
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
                or OperationCanceledException or InvalidOperationException)
            {
                update = null;
            }

            if (installAttempt != clientUpdateInstallAttempt)
                return;

            availableClientUpdate = update;
            context.UpdateStatus = update is null ? "" : $"Доступна версия {update.Version}.";
            context.ClientUpdateAvailable = update is not null;
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (!context.CanInstallClientUpdate || availableClientUpdate is not { } update)
                return;

            clientUpdateInstallAttempt++;
            context.IsInstallingClientUpdate = true;
            var showingProgress = false;
            try
            {
                context.UpdateStatus = $"Доступна версия {update.Version}.";
                var accepted = await PopupDialog.Show(
                    $"Установить версию {update.Version}? Castar перезапустится.",
                    PopupDialog.TypeDialogEnum.Confirm, options =>
                    {
                        options.Title = "Обновление";
                        options.SuccessButton.SuccessConfirmButtonText = "Установить";
                        options.AllowMissClick = false;
                        return options;
                    });
                if (accepted != true)
                    return;

                await ButtonLoading.RunAsync(sender, async () =>
                {
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
                });
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                or OperationCanceledException or Win32Exception or InvalidOperationException)
            {
                context.UpdateStatus = "Не удалось обновить Castar. Повторите попытку.";
            }
            finally
            {
                showingProgress = false;
                context.IsDownloadingUpdate = false;
                context.IsInstallingClientUpdate = false;
            }
        }
    }
}
