/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SerialPortSpy.Interop
{
    /// <summary>
    /// Turns on the native dark title bar via DWM. Silently no-ops (title bar
    /// stays light) on Windows versions that don't support the attribute.
    /// Call from Window.OnSourceInitialized, when the HWND exists but the
    /// window has not painted yet, to avoid a light-to-dark flash.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;             // Win10 20H1 (19041)+ / Win11
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19; // Win10 1809-1909 (undocumented)

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
            }
        }
    }
}
