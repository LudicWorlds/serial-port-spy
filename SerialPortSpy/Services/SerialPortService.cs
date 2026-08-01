/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using SerialPortSpy.Models;

namespace SerialPortSpy.Services
{
    /// <summary>
    /// Wraps System.IO.Ports.SerialPort so the ViewModel never touches the port directly.
    /// </summary>
    public class SerialPortService : IDisposable
    {
        //Not readonly: a surprise-removed device can leave the instance wedged
        //(see ForceClose), and the only reliable cure is to throw it away.
        private SerialPort _serialPort = new SerialPort();

        //Captured at Open() while the BaseStream getter still works. On surprise
        //removal the package's event loop marks the stream "not open" WITHOUT
        //closing the OS handle, and from then on every release path is gated off:
        //SerialPort.Close()/Dispose() skip the stream when !IsOpen, and the
        //BaseStream getter throws. Disposing this captured reference is the one
        //path that still frees the handle (SerialStream.Dispose closes it in a
        //finally, even on a dead device). While the handle leaks, the driver
        //keeps the SERIALCOMM registry entry alive, so GetPortNames() keeps
        //listing the unplugged port.
        private Stream _baseStream;

        public bool IsOpen => _serialPort.IsOpen;

        public int BytesToRead => _serialPort.BytesToRead;

        public string[] GetPortNames()
        {
            return SerialPort.GetPortNames();
        }

        /// <summary>
        /// True when the underlying device is still responding. Reading the CTS
        /// pin issues a modem-status IOCTL to the driver, which fails once the
        /// hardware has been surprise-removed - even if GetPortNames() still
        /// lists the port (some drivers keep the SERIALCOMM registry entry alive
        /// while a handle is open) and the handle still reports open.
        /// </summary>
        public bool IsDeviceResponsive()
        {
            try
            {
                if (!_serialPort.IsOpen) return false;

                _ = _serialPort.CtsHolding;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Open(SerialPortSettings settings)
        {
            _serialPort.PortName = settings.PortName;
            _serialPort.BaudRate = settings.BaudRate;
            _serialPort.Parity = settings.Parity;
            _serialPort.StopBits = settings.StopBits;

            //Options not exposed in UI
            _serialPort.Handshake = Handshake.None;
            _serialPort.DataBits = 8;
            _serialPort.Encoding = Encoding.Default;

            _serialPort.Open();
            _baseStream = _serialPort.BaseStream;

            _baseStream.Flush();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
        }

        /// <summary>
        /// Closes the port. Never throws: if the normal path fails - or the
        /// stream already marked itself closed after a surprise removal, which
        /// makes the BaseStream getter throw immediately - fall back to
        /// ForceClose(), which releases the OS handle via the captured stream.
        /// </summary>
        public void Close()
        {
            try
            {
                _serialPort.BaseStream.Flush();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                _serialPort.Close();
                _baseStream = null;
            }
            catch
            {
                ForceClose();
            }
        }

        /// <summary>
        /// Releases the port when the normal Close() cannot (see _baseStream).
        /// Disposing the captured stream closes the OS handle - SerialStream.
        /// Dispose does that in a finally, so it works even when the device is
        /// gone. The wedged SerialPort instance is then discarded for a fresh
        /// one; there is no way to reset its internal state.
        /// </summary>
        private void ForceClose()
        {
            try { _baseStream?.Dispose(); }
            catch { /* the handle is closed in its finally regardless */ }
            _baseStream = null;

            try { _serialPort.Dispose(); }
            catch { /* stream already handled above */ }
            _serialPort = new SerialPort();
        }

        /// <summary>
        /// Reads all currently buffered bytes from the port.
        /// </summary>
        public byte[] ReadAvailableBytes()
        {
            byte[] data = new byte[_serialPort.BytesToRead];
            _serialPort.Read(data, 0, data.Length);
            return data;
        }

        /// <summary>
        /// Reads all currently buffered data as text (uses the port's encoding).
        /// </summary>
        public string ReadExisting()
        {
            return _serialPort.ReadExisting();
        }

        public void Dispose()
        {
            //SerialPort.Dispose() throws on a device removed mid-close, and
            //silently skips the stream when it already marked itself closed - so
            //also dispose the captured stream (idempotent; no-op after a normal
            //Close()). This runs while the window is closing, where an escaping
            //exception would be an unhandled crash on exit.
            try { _serialPort.Dispose(); }
            catch { /* device already gone */ }

            try { _baseStream?.Dispose(); }
            catch { /* the handle is freed in its finally regardless */ }
            _baseStream = null;
        }
    }
}
