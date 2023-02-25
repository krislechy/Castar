using System.Windows.Forms;
namespace Castar.Tray
{
    public class Tray : IDisposable
    {
        NotifyIcon icon;
        public Tray()
        {
            icon = new NotifyIcon()
            {
                Icon = new Icon("pack://application:,,,/Castar;component/logo.ico"),
                Text = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name,
                Visible= true,
            };
        }

        public void Dispose()
        {
            icon.Dispose();
        }
    }
}