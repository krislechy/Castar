using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Castar.Classes
{
    public class MessageToApplication
    {
        public class RegisterHook
        {
            public static void AddHook()
            {
                HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(Application.Current.MainWindow).Handle);
                source.AddHook(new HwndSourceHook(WndProc));
            }
            private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                System.Windows.Forms.Message m = System.Windows.Forms.Message.Create(hwnd, msg, wParam, lParam);
                if (m.Msg == WM_COPYDATA)
                {
                    COPYDATASTRUCT cds = (COPYDATASTRUCT)m.GetLParam(typeof(COPYDATASTRUCT));
                    if (cds.cbData == Marshal.SizeOf(typeof(MyStruct)))
                    {
                        MyStruct myStruct = (MyStruct)Marshal.PtrToStructure(cds.lpData,
                            typeof(MyStruct));
                        switch (myStruct.Message)
                        {
                            case ("Show"):
                                {
                                    Application.Current.MainWindow.Show();
                                    break;
                                }
                        }
                    }
                }
                return IntPtr.Zero;
            }

            internal const int WM_COPYDATA = 0x004A;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct MyStruct
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
                public string Message;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct COPYDATASTRUCT
            {
                public IntPtr dwData;       // Specifies data to be passed
                public int cbData;          // Specifies the data size in bytes
                public IntPtr lpData;       // Pointer to data to be passed
            }
        }
        public class SendMessage
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct MyStruct
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
                public string Message;
            }

            internal const int WM_COPYDATA = 0x004A;
            [StructLayout(LayoutKind.Sequential)]
            internal struct COPYDATASTRUCT
            {
                public IntPtr dwData;       // Specifies data to be passed
                public int cbData;          // Specifies the data size in bytes
                public IntPtr lpData;       // Pointer to data to be passed
            }
            [SuppressUnmanagedCodeSecurity]
            internal class NativeMethod
            {
                [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
                public static extern IntPtr SendMessage(IntPtr hWnd, int Msg,
                    IntPtr wParam, ref COPYDATASTRUCT lParam);


                [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
                public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
            }

            public static void Send(string text, IntPtr hTargetWnd)
            {
                if (System.Diagnostics.Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Count() > 0)
                {
                    if (hTargetWnd == IntPtr.Zero)
                    {
                        return;
                    }
                    MyStruct myStruct;
                    myStruct.Message =text;
                    int myStructSize = Marshal.SizeOf(myStruct);
                    IntPtr pMyStruct = Marshal.AllocHGlobal(myStructSize);
                    try
                    {
                        Marshal.StructureToPtr(myStruct, pMyStruct, true);

                        COPYDATASTRUCT cds = new COPYDATASTRUCT();
                        cds.cbData = myStructSize;
                        cds.lpData = pMyStruct;
                        NativeMethod.SendMessage(hTargetWnd, WM_COPYDATA, new IntPtr(), ref cds);

                        int result = Marshal.GetLastWin32Error();
                        if (result != 0)
                        {
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pMyStruct);
                    }
                    Application.Current.Shutdown(0);
                }
            }
        }
    }
}
