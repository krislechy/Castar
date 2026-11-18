using Castar.Compontents;
using Castar.Logger;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Castar;

public partial class App
{
    private Task<bool?>? unhandledErrorDialog;

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        cLogger.Log<App>($"Необработанное исключение процесса. Завершение: {e.IsTerminating}.{Environment.NewLine}{e.ExceptionObject}",
            TypeLog.Critical);

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        cLogger.Log<App>($"Необработанное исключение фоновой задачи.{Environment.NewLine}{e.Exception}", TypeLog.Error);

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        cLogger.Log<App>($"Необработанное исключение интерфейса.{Environment.NewLine}{e.Exception}", TypeLog.Critical);
        if (MainWindow is not MainWindow { IsVisible: true } || Dispatcher.HasShutdownStarted)
            return;

        e.Handled = true;
        if (unhandledErrorDialog?.IsCompleted != false)
            unhandledErrorDialog = PopupDialog.Show($"Ошибка приложения. Журнал{Environment.NewLine}{LogPath}",
                PopupDialog.TypeDialogEnum.Default, options =>
                {
                    options.AllowMissClick = false;
                    return options;
                });
    }
}
