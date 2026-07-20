/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using SerialPortSpy.Models;
using SerialPortSpy.Services;

namespace SerialPortSpy.ViewModels
{
    /// <summary>
    /// ViewModel for MainWindow. Owns the serial port service, the polling timer,
    /// and all UI state except the RichTextBox rendering (which is view logic).
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private const string READY_STATUS_MSG = "Click 'Open Port' to start reading incoming data.";
        private const string INVALID_BAUD_MSG = "Baud rate must be a whole number between 1 and 4,000,000.";

        //The SerialPort BaudRate setter only rejects values <= 0; this ceiling is a
        //typo guard, set above the ~3 Mbaud max of common FTDI/CP2102 bridges. An
        //in-range rate the hardware can't do still fails later, at Open().
        private const int MIN_BAUD = 1;
        private const int MAX_BAUD = 4_000_000;

        //These strings are both the combo's items and the switch labels in
        //OnSerialTimer_Tick - named so the two can never drift apart.
        //"Text" rather than "ASCII": the port decodes with Encoding.Default,
        //which is UTF-8 on .NET 10, so this was never strictly ASCII.
        private const string DISPLAY_TEXT = "Text";
        private const string DISPLAY_DECIMAL = "Decimal";
        private const string DISPLAY_HEX = "Hex";

        private readonly SerialPortService _serialPortService;
        private readonly DispatcherTimer _serialTimer;

        private string _selectedPortName;
        private string _baudRateText;
        private Parity _selectedParity;
        private StopBits _selectedStopBits;
        private string _selectedDisplayDataOption;
        private string _statusMessage;
        private bool _isPortOpen;
        private bool _isError;

        /// <summary>
        /// Raised when a chunk of serial data has been received and formatted for display.
        /// </summary>
        public event EventHandler<string> DataReceived;

        /// <summary>
        /// Raised when the data display should be cleared (i.e. when a port is opened).
        /// </summary>
        public event EventHandler OutputCleared;

        public MainViewModel()
        {
            _serialPortService = new SerialPortService();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"Serial Port Spy - v{v.Major}.{v.Minor}";

            //Covers the rates a microcontroller monitor actually meets, including
            //74880 (ESP8266 boot log), 250000 (Marlin / DMX512) and the fast ESP32
            //rates. The modem-era 300/600/14400/28800 are gone. The slow rates that
            //remain earn their place: 4800 is the NMEA 0183 standard (GPS modules),
            //and 1200 is the 'touch' that resets native-USB Arduinos (Leonardo,
            //Micro, Zero, MKR) into their bootloader. The combo is editable, so
            //anything omitted can still be typed.
            BaudRates = new ObservableCollection<string>(
                new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200",
                        "230400", "250000", "500000", "921600", "1000000", "2000000" });
            ParityOptions = Enum.GetValues(typeof(Parity)).Cast<Parity>().ToArray();
            StopBitsOptions = Enum.GetValues(typeof(StopBits)).Cast<StopBits>().ToArray();
            //Ordered by how often an Arduino hobbyist wants each: Serial.println()
            //is text, then raw byte values, then hex for protocol work.
            DisplayDataOptions = new[] { DISPLAY_TEXT, DISPLAY_DECIMAL, DISPLAY_HEX };

            _baudRateText = "9600";
            _selectedParity = Parity.None;
            _selectedStopBits = StopBits.One;
            //Explicit, not DisplayDataOptions[0] - reordering the combo must not
            //silently change which mode the app starts in. Text matches what the
            //Arduino IDE's Serial Monitor shows.
            _selectedDisplayDataOption = DISPLAY_TEXT;
            _statusMessage = READY_STATUS_MSG;

            PortNames = new ObservableCollection<string>();
            RefreshPortNames();

            _serialTimer = new DispatcherTimer();
            _serialTimer.Tick += OnSerialTimer_Tick;
            _serialTimer.Interval = TimeSpan.FromMilliseconds(1); //Query Serial Data every millisecond

            //The IsPortOpen clause matters: once open the button means 'Close Port',
            //which must stay clickable no matter what is in the baud box.
            //RelayCommand routes CanExecuteChanged to CommandManager.RequerySuggested,
            //which WPF raises on keyboard input, so this re-evaluates as the user types.
            TogglePortCommand = new RelayCommand(
                TogglePort,
                () => !string.IsNullOrEmpty(SelectedPortName) && (IsPortOpen || TryGetBaudRate(out _)));
        }

        //-----------------------------------------------
        // Bindable properties
        //-----------------------------------------------

        public string Title { get; }

        public ObservableCollection<string> PortNames { get; }

        public ObservableCollection<string> BaudRates { get; }

        public Parity[] ParityOptions { get; }

        public StopBits[] StopBitsOptions { get; }

        public string[] DisplayDataOptions { get; }

        public ICommand TogglePortCommand { get; }

        public string SelectedPortName
        {
            get => _selectedPortName;
            set => SetProperty(ref _selectedPortName, value);
        }

        public string BaudRateText
        {
            get => _baudRateText;
            set
            {
                if (SetProperty(ref _baudRateText, value))
                {
                    UpdateBaudRateStatus();
                }
            }
        }

        public Parity SelectedParity
        {
            get => _selectedParity;
            set => SetProperty(ref _selectedParity, value);
        }

        public StopBits SelectedStopBits
        {
            get => _selectedStopBits;
            set => SetProperty(ref _selectedStopBits, value);
        }

        public string SelectedDisplayDataOption
        {
            get => _selectedDisplayDataOption;
            set => SetProperty(ref _selectedDisplayDataOption, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsPortOpen
        {
            get => _isPortOpen;
            private set
            {
                if (SetProperty(ref _isPortOpen, value))
                {
                    OnPropertyChanged(nameof(IsConfigEnabled));
                    OnPropertyChanged(nameof(TogglePortButtonText));
                }
            }
        }

        /// <summary>
        /// True while the last open/close attempt failed; drives the red
        /// status-bar dot. Cleared at the start of each new attempt.
        /// </summary>
        public bool IsError
        {
            get => _isError;
            private set => SetProperty(ref _isError, value);
        }

        /// <summary>
        /// Gates the four *serial* parameters (port, baud, parity, stop bits),
        /// which cannot change on an open port. Display mode is deliberately not
        /// gated by this - it only affects how bytes are rendered, so it stays
        /// switchable mid-session.
        /// </summary>
        public bool IsConfigEnabled => !IsPortOpen;

        public string TogglePortButtonText => IsPortOpen ? "Close Port" : "Open Port";

        //-----------------------------------------------
        // Methods
        //-----------------------------------------------

        public void RefreshPortNames()
        {
            PortNames.Clear();

            foreach (string name in _serialPortService.GetPortNames())
            {
                PortNames.Add(name);
            }

            SelectedPortName = PortNames.FirstOrDefault();
        }

        /// <summary>
        /// Called by the view when the window is closing.
        /// </summary>
        public void Shutdown()
        {
            if (_serialPortService.IsOpen)
            {
                ClosePort();
            }

            _serialPortService.Dispose();
        }

        /// <summary>
        /// Parses the typed baud rate. NumberStyles.None with InvariantCulture so
        /// "-5", "+5", "1,000" and culture-specific separators are all rejected the
        /// same way on every machine.
        /// </summary>
        private bool TryGetBaudRate(out int baudRate)
        {
            return int.TryParse(BaudRateText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out baudRate)
                   && baudRate >= MIN_BAUD
                   && baudRate <= MAX_BAUD;
        }

        /// <summary>
        /// Explains a greyed-out 'Open Port' button. Deliberately does not set
        /// IsError: a half-typed value is not a failed operation, so the status dot
        /// stays idle grey and red keeps meaning "the last open/close threw".
        /// </summary>
        private void UpdateBaudRateStatus()
        {
            if (IsPortOpen) return; //The combo is disabled while open

            IsError = false; //Editing the config voids the previous attempt's result
            StatusMessage = TryGetBaudRate(out _) ? READY_STATUS_MSG : INVALID_BAUD_MSG;
        }

        private void TogglePort()
        {
            if (_serialPortService.IsOpen)
            {
                ClosePort();
            }
            else
            {
                OutputCleared?.Invoke(this, EventArgs.Empty);
                OpenPort();
            }
        }

        private bool OpenPort()
        {
            try
            {
                IsError = false;
                StatusMessage = "Trying to open " + SelectedPortName + " ...";

                //Already guarded by the command's CanExecute; re-checked here so this
                //never reaches the SerialPort setter with an unvalidated value.
                if (!TryGetBaudRate(out int baudRate))
                {
                    //Unlike typing, this is a failed user action - so it does go red.
                    StatusMessage = INVALID_BAUD_MSG;
                    IsError = true;
                    return false;
                }

                var settings = new SerialPortSettings
                {
                    PortName = SelectedPortName,
                    BaudRate = baudRate,
                    Parity = SelectedParity,
                    StopBits = SelectedStopBits
                };

                _serialPortService.Open(settings);
                _serialTimer.Start();

                StatusMessage = settings.PortName + " successfully opened. Reading incoming bytes at " + settings.BaudRate + " bps.";
            }
            catch (Exception error)
            {
                StatusMessage = error.Message;
                IsError = true;
                return false;
            }
            finally
            {
                IsPortOpen = _serialPortService.IsOpen;
            }

            return true;
        }

        private bool ClosePort()
        {
            try
            {
                IsError = false;
                StatusMessage = "Trying to close " + SelectedPortName + " ...";

                _serialTimer.Stop();
                _serialPortService.Close();

                StatusMessage = READY_STATUS_MSG;
            }
            catch (Exception error)
            {
                StatusMessage = error.Message;
                IsError = true;
                return false;
            }
            finally
            {
                IsPortOpen = _serialPortService.IsOpen;
            }

            return true;
        }

        //------------------------------------------------------
        // EventHandlers
        //------------------------------------------------------

        private void OnSerialTimer_Tick(object sender, EventArgs e)
        {
            if (!_serialPortService.IsOpen || _serialPortService.BytesToRead < 1) return;

            string receivedString;

            switch (SelectedDisplayDataOption)
            {
                case DISPLAY_DECIMAL:
                    //D3 - fixed width so columns line up in the log
                    receivedString = FormatBytes(_serialPortService.ReadAvailableBytes(), "D3");
                    break;

                case DISPLAY_HEX:
                    //X2 - uppercase, zero-padded, the conventional hex-dump form
                    receivedString = FormatBytes(_serialPortService.ReadAvailableBytes(), "X2");
                    break;

                case DISPLAY_TEXT:
                default:
                    //Decoded with the port's Encoding.Default (UTF-8 on .NET 10),
                    //so bytes 0x80-0xFF arrive as U+FFFD rather than one glyph
                    //each - use Hex or Decimal to inspect non-text traffic.
                    receivedString = _serialPortService.ReadExisting();
                    break;
            }

            DataReceived?.Invoke(this, receivedString);
        }

        /// <summary>
        /// Renders bytes as fixed-width, space-separated tokens. InvariantCulture
        /// so a culture with non-Latin native digits can never reshape a data dump.
        /// </summary>
        private static string FormatBytes(byte[] data, string format)
        {
            //4 chars per byte covers the widest token ("D3" plus its separator)
            var builder = new StringBuilder(data.Length * 4);

            foreach (byte b in data)
            {
                builder.Append(b.ToString(format, CultureInfo.InvariantCulture));
                builder.Append(' ');
            }

            return builder.ToString();
        }
    }
}
