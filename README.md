# Serial Port Spy

![Screenshot](serial-port-spy.png)

**Serial Port Spy** is a simple Windows application for reading incoming **RS232 / serial data** received via a **COM port**. A common use case is monitoring data transmitted from a microcontroller, such as an Arduino.


## 🔧 How to Use

1. In the **Releases** section, go to the latest release.
2. Download and extract: **SerialPortSpy.zip**
3. Run: **SerialPortSpy.exe**
4. Select the serial port you wish to read from using the **COM Port Name** dropdown.  
5. Configure the port properties (e.g., **Baud Rate**, **Parity**, and **Stop Bits**).  
6. Click the **Open Port** button.

You can view incoming data as either:
- **Decimal values**
- **ASCII text**


## Technical Details

The application is written in **C# / .NET Framework 4.8**, and built as a **WPF** application. It can be compiled using **Visual Studio 2022** (or newer). It is currently Windows only.

This project is released under the [MIT License](LICENSE).


## ⚠️ Disclaimer

This project is provided "as is" without warranty of any kind. Use at your own risk. The author is not responsible for any damage or data loss resulting from the use of this package. Compatibility and performance may vary depending on your system configuration.
