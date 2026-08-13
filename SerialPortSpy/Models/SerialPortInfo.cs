/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

namespace SerialPortSpy.Models
{
    /// <summary>
    /// One COM port as offered in the dropdown: the name Windows opens it by,
    /// plus the device description behind it ("USB-SERIAL CH340"). Description
    /// is null or empty when Windows reports no friendly name for the port.
    ///
    /// IsUsbDevice is named for the fact it records - the bus the port hangs
    /// off - and not for the inference the UI draws from it. Nothing can tell a
    /// microcontroller from anything else behind a USB-serial bridge, so the
    /// bus is used as a proxy for "this could be a board worth monitoring":
    /// the view decorates USB ports and lets the rest recede. Description stays
    /// populated either way, because the status bar shows it regardless.
    ///
    /// A record, not a plain class like SerialPortSettings, and the value
    /// equality is load-bearing: MainViewModel.RefreshPortNames() reconciles
    /// the list in place with Contains/!=, so with reference equality every
    /// refresh would rebuild the whole list and make the ComboBox selection
    /// flicker on each device-change tick. Do not "tidy" this into a class.
    /// </summary>
    public sealed record SerialPortInfo(string PortName, string Description, bool IsUsbDevice);
}
