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
        private readonly Icon icon;
        private readonly string name;
        private readonly EventHandler onClick;
        private readonly EventHandler? onContextMenu;
        private bool keepVisible;
        private System.Windows.Forms.Timer? notificationTimer;

        public TrayIcon(Icon icon, string name, EventHandler onClick, EventHandler? onContextMenu = null)
        {
            this.icon = icon;
            this.name = name;
            this.onClick = onClick;
            this.onContextMenu = onContextMenu;
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
                notifyIcon.MouseClick += NotifyIcon_MouseClick;
            }

            notifyIcon.Visible = true;
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                onClick(sender, e);
            else if (e.Button == MouseButtons.Right)
                onContextMenu?.Invoke(sender, e);
        }

        public void Dispose()
        {
            keepVisible = false;
            notificationTimer?.Dispose();
            notificationTimer = null;
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.MouseClick -= NotifyIcon_MouseClick;
            }
            notifyIcon?.Dispose();
            notifyIcon = null;
        }
    }
}
