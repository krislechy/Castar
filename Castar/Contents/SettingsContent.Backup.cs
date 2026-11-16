using Castar.ServerConnection;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Castar.Contents;

public partial class SettingsContent
{
    private CancellationTokenSource? backupRequest;

    private async void CreateDatabaseBackup_Click(object sender, RoutedEventArgs e)
    {
        if (backupRequest is { } active)
        {
            active.Cancel();
            return;
        }

        var chooser = new SaveFileDialog
        {
            FileName = $"Castar-backup-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite",
            Filter = "База SQLite (*.sqlite)|*.sqlite",
            DefaultExt = ".sqlite",
            AddExtension = true
        };
        if (chooser.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var viewModel = context.ServerConfiguration;
        var label = viewModel.BackupActionLabel;
        using var request = new CancellationTokenSource();
        backupRequest = request;
        viewModel.BackupActionLabel = "Отменить";
        viewModel.BackupStatus = "Создание резервной копии…";
        try
        {
            await server.DownloadDatabaseBackupAsync(chooser.FileName, request.Token);
            viewModel.BackupStatus = "";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            viewModel.BackupStatus = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            viewModel.BackupStatus = "Не удалось сохранить резервную копию.";
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            viewModel.BackupStatus = CastarServerClient.DescribeError(error);
        }
        finally
        {
            backupRequest = null;
            viewModel.BackupActionLabel = label;
        }
    }
}
