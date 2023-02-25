using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL.Settings.Autorun
{
    public sealed class Autorun:IDisposable, IAutorun
    {
        private readonly string appName;
        private readonly string execPathApp;
        private readonly RegistryKey registry;
        public Autorun(string appName, string execPathApp, string regPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run")
        {
            if (appName == null) throw new ArgumentNullException(nameof(appName));
            if (execPathApp == null) throw new ArgumentNullException(nameof(execPathApp));
            if (regPath == null) throw new ArgumentNullException(nameof(regPath));
            this.appName = appName;
            this.execPathApp = execPathApp;
            this.registry = Registry.CurrentUser.OpenSubKey(regPath, true);
        }

        public bool IsStartUp() =>
            registry.GetValue(appName) != null;
        public void RemoveFromStartUp()
        {
            if (IsStartUp())
                registry.DeleteValue(appName, false);
        }
        public void AddToStartUp()
        {
            if (!IsStartUp())
                registry.SetValue(appName, execPathApp);
        }

        public void Dispose()
        {
            var gen=GC.GetGeneration(this);
            GC.Collect(gen);
        }
    }
}
