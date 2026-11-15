using System.Windows.Forms;
namespace Castar.TrayNotify.Windows
{
    public interface ITrayIcon
    {
        public void Show();
        public void Dispose();
    }
    public class TrayIcon : IDisposable, ITrayIcon
    {
        NotifyIcon? notifyIcon;
        Icon icon;
        string name;
        EventHandler onClick;

        public TrayIcon(Icon Icon, string Name, EventHandler onClick)
        {
            this.icon = Icon;
            this.name = Name;
            this.onClick = onClick;
        }
        public void Show()
        {
            if (notifyIcon is null)
            {
                notifyIcon = new NotifyIcon { Icon = icon, Text = name };
                notifyIcon.Click += onClick;
            }

            notifyIcon.Visible = true;
        }
        public void Dispose()
        {
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Click -= onClick;
            }
            notifyIcon?.Dispose();
            notifyIcon = null;
        }
    }
}
