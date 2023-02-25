using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Externs
{
    internal class ScalingDpi
    {
        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private enum DeviceCap
        {
            VERTRES = 10,
            DESKTOPVERTRES = 117,
            LOGPIXELSY = 90,
        }


        public static double GetScalingFactor()
        {
            Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            IntPtr desktop = g.GetHdc();
            int LogicalScreenHeight = GetDeviceCaps(desktop, (int)DeviceCap.VERTRES);
            int PhysicalScreenHeight = GetDeviceCaps(desktop, (int)DeviceCap.DESKTOPVERTRES);
            int logpixelsy = GetDeviceCaps(desktop, (int)DeviceCap.LOGPIXELSY);
            float screenScalingFactor = (float)PhysicalScreenHeight / (float)LogicalScreenHeight;
            float dpiScalingFactor = (float)logpixelsy / (float)96;

            if (screenScalingFactor > 1 ||
                dpiScalingFactor > 1)
            {
                var coef_screenScalingFactor = (double)1 - screenScalingFactor;
                return coef_screenScalingFactor + dpiScalingFactor;
            }
            return 1;
        }
    }
}
