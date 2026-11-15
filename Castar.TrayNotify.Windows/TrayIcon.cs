using System.Windows.Forms;
namespace Castar.TrayNotify.Windows
{
    public interface ITrayIcon
    {
        public void Show();
        public void Notify(string message);
        public void Dispose();
    }
    public class TrayIcon : IDisposable, ITrayIcon
    {
        NotifyIcon? notifyIcon;
        Icon icon;
        string name;
        EventHandler onClick;
        private bool keepVisible;
        private System.Windows.Forms.Timer? notificationTimer;

        public TrayIcon(Icon Icon, string Name, EventHandler onClick)
        {
            this.icon = Icon;
            this.name = Name;
            this.onClick = onClick;
        }
        public void Show()
        {
            keepVisible = true;
            EnsureVisible();
        }

        public void Notify(string message)
        {
            EnsureVisible();
            notifyIcon!.ShowBalloonTip(5000, name, message, ToolTipIcon.Info);
            if (keepVisible)
                return;

            if (notificationTimer is null)
            {
                notificationTimer = new System.Windows.Forms.Timer { Interval = 10000 };
                notificationTimer.Tick += (_, _) =>
                {
                    notificationTimer?.Stop();
                    if (!keepVisible)
                        Dispose();
                };
            }
            notificationTimer.Stop();
            notificationTimer.Start();
        }

        private void EnsureVisible()
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
            keepVisible = false;
            notificationTimer?.Dispose();
            notificationTimer = null;
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
