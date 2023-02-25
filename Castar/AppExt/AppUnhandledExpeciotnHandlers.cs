using Castar.Compontents;
using Castar.Logger;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
                    var content = new TextBlock();
                    var head = new Run()
                    {
                        Foreground = new SolidColorBrush(Colors.OrangeRed),
                        Text = "Необработанное исключение:\n"
                    };
                    var bottom = new Run(exception.ToString());
                    content.Inlines.Add(head);
                    content.Inlines.Add(bottom);
                    await PopupDialog.Show(content, PopupDialog.TypeDialogEnum.Default, (e) =>
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

        private void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            ExceptionsHandler(e.Exception);
        }
    }
}
