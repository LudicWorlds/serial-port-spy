/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;

namespace SerialPortSpy.Interop
{
    /// <summary>
    /// Constants and a predicate for the Windows device-change broadcast.
    /// Windows sends WM_DEVICECHANGE with DBT_DEVNODES_CHANGED to every top-level
    /// window whenever the device tree changes - no RegisterDeviceNotification
    /// call is needed for this coarse notification. It doesn't say *what* changed,
    /// so the handler re-enumerates the COM ports and reconciles the list.
    /// Hook it from a Window's WndProc (see MainWindow.OnSourceInitialized).
    /// </summary>
    internal static class DeviceNotifications
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        /// <summary>
        /// True when the message is the coarse "a device was added or removed"
        /// broadcast - the cue to refresh the COM-port list.
        /// </summary>
        public static bool IsPortListChange(int msg, IntPtr wParam)
        {
            //ToInt64, not ToInt32: wParam is pointer-sized, and the narrowing
            //conversion throws on overflow. The DBT_* values are all small, but
            //this way the guard can't depend on that.
            return msg == WM_DEVICECHANGE && wParam.ToInt64() == DBT_DEVNODES_CHANGED;
        }
    }
}
