/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
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
        private readonly SerialPort _serialPort = new SerialPort();

        public bool IsOpen => _serialPort.IsOpen;

        public int BytesToRead => _serialPort.BytesToRead;

        public string[] GetPortNames()
        {
            return SerialPort.GetPortNames();
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
            _serialPort.BaseStream.Flush();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
        }

        public void Close()
        {
            _serialPort.BaseStream.Flush();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
            _serialPort.Close();
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
            _serialPort.Dispose();
        }
    }
}
