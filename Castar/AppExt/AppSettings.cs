using Castar.BL.Settings;
using Castar.BL.Settings.Autorun;
using Castar.Logger;
using Castar.TrayNotify.Windows;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Castar
{
    public partial class App : Application
    {
        private void ResolveSettings(ServiceProvider serviceProvider)
        {
            ISettingsService settingsService = serviceProvider.GetService<ISettingsService>();
            var trayIconinstance = serviceProvider.GetService<ITrayIcon>();
            var autorunInstance = serviceProvider.GetService<IAutorun>();
            settingsService.Get();
            settingsService.Dispose(() => trayIconinstance.Dispose());
            settingsService.Apply((e) =>
            {
                if (e == null) return;
                if (e.Systems.IsHideInTray)
                {
                    trayIconinstance.Show();
                }
                else
                {
                    trayIconinstance.Dispose();
                }
                if (e.Systems.IsAutoRun != autorunInstance.IsStartUp())
                {
                    if (e.Systems.IsAutoRun)
                    {
                        autorunInstance.AddToStartUp();
                    }
                    else
                    {
                        autorunInstance.RemoveFromStartUp();
                    }
                }
            });
        }
        private ITrayIcon GetTrayIconInstance()
        {
            return new TrayIcon(
                Icon: System.Drawing.Icon.ExtractAssociatedIcon(GetExecutionPathToBinaryExe()),
                Name: System.Reflection.Assembly.GetExecutingAssembly().GetName().Name,
                onClick: (q, e) =>
                {
                    Current.MainWindow.Show();
                    Current.MainWindow.Activate();
                });
        }
        private IAutorun GetAutorunInstance()
        {
            return new Autorun(
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Name,
                GetExecutionPathToBinaryExe()
                );
        }
        private string GetExecutionPathToBinaryExe() =>
            AppDomain.CurrentDomain.BaseDirectory + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".exe";
    }
}
