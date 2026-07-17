/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System.IO.Ports;

namespace SerialPortSpy.Models
{
    /// <summary>
    /// User-configurable serial port settings.
    /// </summary>
    public class SerialPortSettings
    {
        public string PortName { get; set; }
        public int BaudRate { get; set; }
        public Parity Parity { get; set; }
        public StopBits StopBits { get; set; }
    }
}
