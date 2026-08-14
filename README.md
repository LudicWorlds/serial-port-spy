# Serial Port Spy

![Screenshot](serial-port-spy.png)

**Serial Port Spy** is a simple Windows application for reading incoming **RS232 / serial data** received via a **COM port**. A common use case is monitoring data transmitted from a microcontroller, such as an Arduino.

It is currently **read-only** — it listens, and never transmits.


## 🔧 How to Use

1. In the **Releases** section, go to the latest release.
2. Download **SerialPortSpy.zip** and extract it anywhere you like.
3. Run the extracted **SerialPortSpy.exe**.
4. Select the port you wish to read from using the **COM Port** dropdown. Ports arriving over USB are labelled with the device behind them — `Arduino Uno`, `USB-SERIAL CH340` — so a board stands out from the Bluetooth and on-board ports, which are listed dimmed.
5. Configure **Baud Rate**, **Parity**, and **Stop Bits** to match your device.
6. Choose how to view the data with **Display Data As**.
7. Click **Open Port**.

**Requires 64-bit Windows.** The download is an x64 build and will not run on 32-bit Windows.

There is no installer and nothing to set up — the `.exe` is fully self-contained, so you don't need the .NET runtime installed. It can be run from anywhere, and deleting it uninstalls it.

The app isn't code-signed, so Windows will likely show a **"Windows protected your PC"** dialog the first time you run it. Choose **More info → Run anyway**.

At least one COM port must be present — if none is found on launch, the app says so and exits.

Opening a port clears the output, so each session starts clean. The button then becomes **Close Port**.

To wipe the output mid-session, use the **Clear** button beneath the output pane, press `Ctrl+K`, or right-click the log and choose **Clear**. It blanks the log and the plotter together, and works whether or not a port is open — handy for clearing accumulated noise and then watching fresh data arrive.

The status bar along the bottom reports what happened, alongside a state dot:

| Dot | Meaning |
| --- | --- |
| ⚪ Grey | Idle — no port open |
| 🟢 Green | Port open, reading |
| 🔴 Red | The last open or close attempt failed |

### Baud Rate

The dropdown lists the common rates from **1200** to **2000000**, but the box is also **editable** — type any whole number from 1 to 4,000,000. USB-serial bridges support plenty of rates the list doesn't cover, such as `74880` (ESP8266 boot logs), `250000` (Marlin / DMX512) and `31250` (MIDI).

If what you type isn't a valid rate, **Open Port** greys out and the status bar explains why.


## Output and Plotter

Incoming data is shown in two tabs:

- **Output** — the scrolling text log
- **Plotter** — a live graph of the same bytes

Both are fed continuously while a port is open, so switching between them never leaves a gap in either.


## Display Modes

**Display Data As** controls how incoming bytes are rendered in the **Output** tab. It has no effect on the plotter, which always graphs the raw byte values.

- **Text** — for `Serial.println()` output and anything human-readable
- **Decimal** — each byte as a number, `000`–`255`
- **Hex** — each byte as `00`–`FF`, for protocol and binary work

In Text mode, control bytes are shown as Unicode Control Pictures (`␛` for escape, `␡` for delete) rather than being silently swallowed. These are substitute glyphs, so copying them copies the picture character — **use Hex mode when you need the exact bytes**.

Unlike the serial settings, the display mode can be changed while a port is open. Data already in the log keeps the format it was written with; the change applies from the next bytes onward.


## The Plotter

The **Plotter** tab graphs every received byte as one point on a line, newest entering at the right and older data scrolling off to the left — the same behaviour as the Arduino IDE's own Serial Plotter.

The vertical axis is fixed to the full byte range, `0`–`255`, divided into eight steps of 32 like an oscilloscope graticule. It never rescales, so a given byte value always sits at the same height and a rising trace always means rising values.

The window holds the latest **1000 bytes** — roughly a second of data at 9600 baud. The horizontal axis is deliberately unlabelled, because it measures position within that window rather than time.


## Serial Settings

Four parameters are configurable: **COM port**, **baud rate**, **parity** and **stop bits**. They are locked while a port is open — close it to change them.

Fixed internally, and not exposed in the UI:

- **8 data bits**
- **No handshake / flow control**

Which makes the common `8-N-1` setup the default: 8 data bits, no parity, one stop bit.


## Technical Details

The application is written in **C# / .NET 10** and built as a **WPF** application. It was compiled using **Visual Studio 2026**. It is currently Windows only (64-bit).

The plotter is drawn with [ScottPlot](https://scottplot.net), also MIT licensed.

This project is released under the [MIT License](LICENSE).


## ⚠️ Disclaimer

This project is provided "as is" without warranty of any kind. Use at your own risk. The author is not responsible for any damage or data loss resulting from the use of this package. Compatibility and performance may vary depending on your system configuration.
