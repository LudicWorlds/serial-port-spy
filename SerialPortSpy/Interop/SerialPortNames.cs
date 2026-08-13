/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SerialPortSpy.Interop
{
    /// <summary>
    /// Reads the device friendly names Windows holds for the COM ports, via
    /// SetupAPI - the same source Device Manager's "Ports (COM &amp; LPT)" branch
    /// shows. SerialPort.GetPortNames() only ever returns bare names like
    /// "COM3", which cannot tell an Arduino apart from a Bluetooth serial pair.
    ///
    /// This is decoration only: GetPortNames() remains the sole authority on
    /// *which* ports exist (see SerialPortService), because the whole
    /// surprise-removal design turns on the SERIALCOMM registry key that it
    /// reads. A port this class cannot describe still appears in the list with
    /// its bare name; a port it reports that GetPortNames() doesn't is ignored.
    /// </summary>
    internal static class SerialPortNames
    {
        //The "Ports (COM & LPT)" device setup class. Bluetooth SPP ports,
        //USB-serial bridges and on-board UARTs all live under it.
        private static Guid GUID_DEVCLASS_PORTS = new Guid("4D36E978-E325-11CE-BFC1-08002BE10318");

        private const uint DIGCF_PRESENT = 0x00000002;  //Only devices currently plugged in
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        private const uint SPDRP_ENUMERATOR_NAME = 0x00000016;

        //The bus a port hangs off, as SPDRP_ENUMERATOR_NAME reports it. A board
        //worth monitoring arrives over USB, whether it speaks CDC itself
        //(Arduino, ESP32, Pico) or sits behind a bridge (CH340, CP210x, FTDI).
        //The other enumerators are things this app has no business highlighting:
        //BTHENUM (the Bluetooth SPP pair most machines carry), ROOT (com0com and
        //other virtual null-modems), ACPI/PCI (motherboard UARTs, Intel AMT SOL).
        private const string USB_ENUMERATOR = "USB";

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
                                                         IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
                                                         ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet,
                                                                    ref SP_DEVINFO_DATA deviceInfoData,
                                                                    uint property,
                                                                    out uint propertyRegDataType,
                                                                    byte[] propertyBuffer,
                                                                    uint propertyBufferSize,
                                                                    out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        /// <summary>
        /// Maps port name to what Windows knows about the device behind it:
        /// "COM3" -&gt; ("USB-SERIAL CH340", IsUsb: true). Ports that cannot be
        /// resolved are simply absent from the result - callers fall back to the
        /// bare port name.
        ///
        /// IsUsb records the bus, not the device type: nothing here can tell a
        /// microcontroller from anything else on the far end of a USB-serial
        /// bridge. It is a proxy, and the useful one - a board reaches Windows
        /// over USB, while the ports that clutter the list do not.
        ///
        /// Never throws. Any SetupAPI failure yields an empty dictionary, so a
        /// missing description can never take the COM Port list down with it.
        /// </summary>
        public static Dictionary<string, (string Description, bool IsUsb)> GetDescriptions()
        {
            var descriptions = new Dictionary<string, (string Description, bool IsUsb)>(StringComparer.OrdinalIgnoreCase);
            IntPtr deviceInfoSet = INVALID_HANDLE_VALUE;

            try
            {
                deviceInfoSet = SetupDiGetClassDevs(ref GUID_DEVCLASS_PORTS, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
                if (deviceInfoSet == INVALID_HANDLE_VALUE) return descriptions;

                var deviceInfo = new SP_DEVINFO_DATA();
                deviceInfo.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                for (uint index = 0; SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo); index++)
                {
                    string friendlyName = GetStringProperty(deviceInfoSet, ref deviceInfo, SPDRP_FRIENDLYNAME);
                    if (friendlyName == null) continue;

                    //Windows formats these as "<description> (COMn)", so the port
                    //name has to be split back out of the string it is embedded in.
                    if (!TrySplitFriendlyName(friendlyName, out string portName, out string description)) continue;

                    //A device that will not name its bus is treated as non-USB:
                    //unknown provenance reads the same as uninteresting here.
                    string enumerator = GetStringProperty(deviceInfoSet, ref deviceInfo, SPDRP_ENUMERATOR_NAME);
                    bool isUsb = string.Equals(enumerator, USB_ENUMERATOR, StringComparison.OrdinalIgnoreCase);

                    descriptions[portName] = (description, isUsb);
                }
            }
            catch
            {
                //A P/Invoke that misbehaves must not break the port list - the
                //caller renders bare port names when the map comes back short.
                return descriptions;
            }
            finally
            {
                if (deviceInfoSet != INVALID_HANDLE_VALUE)
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }
            }

            return descriptions;
        }

        /// <summary>
        /// Reads one string device property, or null if the device has none.
        /// Two-call pattern: the first call fails purely to report the size.
        ///
        /// SPDRP_DEVICEDESC is deliberately never used as a fallback for the
        /// friendly name - it carries no "(COMn)" suffix, so there would be no
        /// way to key it to a port.
        /// </summary>
        private static string GetStringProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfo, uint property)
        {
            SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfo, property,
                                             out _, null, 0, out uint requiredSize);

            if (requiredSize < 1) return null;

            byte[] buffer = new byte[requiredSize];

            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfo, property,
                                                  out _, buffer, requiredSize, out _))
            {
                return null;
            }

            //The registry value is a NUL-terminated wide string, and requiredSize
            //counts that terminator - trim it rather than rendering it.
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        /// <summary>
        /// Splits "USB-SERIAL CH340 (COM3)" into "COM3" and "USB-SERIAL CH340".
        /// Returns false when there is no trailing "(COMn)" group, which is the
        /// signal that this device is not a COM port (the Ports class also holds
        /// LPT parallel ports).
        /// </summary>
        private static bool TrySplitFriendlyName(string friendlyName, out string portName, out string description)
        {
            portName = null;
            description = null;

            //LastIndexOf, not IndexOf: a description may contain its own
            //parentheses, e.g. "Prolific USB-to-Serial Comm Port (COM7)".
            if (!friendlyName.EndsWith(")", StringComparison.Ordinal)) return false;

            int open = friendlyName.LastIndexOf('(');
            if (open < 0) return false;

            string inner = friendlyName.Substring(open + 1, friendlyName.Length - open - 2);
            if (!inner.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return false;

            portName = inner;
            description = friendlyName.Substring(0, open).Trim();

            //A name that is nothing but "(COM3)" leaves no description to show.
            return description.Length > 0;
        }
    }
}
