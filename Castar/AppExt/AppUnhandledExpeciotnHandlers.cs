using Castar.Compontents;
using Castar.Logger;
using System;
using System.Linq;
using System.Windows;

namespace Castar
{
    public partial class App: Application
    {
        private readonly string[] whiteListExceptions = new string[] { "Castar.Domain.XmlSerializers" };
        private async void ExceptionsHandler(Exception exception, bool isTerminating = false)
        {
#if (!DEBUG)
            if (!whiteListExceptions.Any(x => exception.Message.Contains(x)))
            {
                cLogger.Log<App>(exception.ToString(), TypeLog.Critical);
                if (!isTerminating)
                {
                    await PopupDialog.Show("Ошибка приложения. См. %LOCALAPPDATA%\\Castar\\Logs\\castar.log.",
                        PopupDialog.TypeDialogEnum.Default, (e) =>
                    {
                        e.AllowMissClick = false;
                        return e;
                    });
                }
            }
#endif
        }
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ExceptionsHandler((Exception)e.ExceptionObject,true);
        }

        private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ExceptionsHandler(e.Exception);
            e.Handled = true;
        }

    }
}
