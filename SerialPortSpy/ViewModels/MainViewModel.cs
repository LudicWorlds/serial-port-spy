/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

        //A burst of WM_DEVICECHANGE messages can arrive for one physical plug, so
        //the refresh is coalesced: each notification restarts this countdown and
        //only the final, quiet tick re-enumerates the ports.
        private static readonly TimeSpan DEVICE_CHANGE_DEBOUNCE = TimeSpan.FromMilliseconds(300);

        private readonly SerialPortService _serialPortService;
        private readonly DispatcherTimer _serialTimer;
        private readonly DispatcherTimer _deviceChangeTimer;

        private string _selectedPortName;
        private string _baudRateText;
        private Parity _selectedParity;
        private StopBits _selectedStopBits;
        private string _selectedDisplayDataOption;
        private string _statusMessage;
        private bool _isPortOpen;
        private bool _isError;

        //The port name currently held open, captured at Open() so a disconnect
        //message can name it even after the SerialPort has been torn down.
        private string _openPortName;

        /// <summary>
        /// Raised when a chunk of serial data has been received and formatted for display.
        /// </summary>
        public event EventHandler<string> DataReceived;

        /// <summary>
        /// Raised when the data display should be cleared: once as a new session
        /// starts (before the port is opened), and whenever the user asks via
        /// ClearOutputCommand. The view tells the two apart by IsPortOpen -
        /// see MainWindow.OnOutputCleared, which resets more on the former.
        /// </summary>
        public event EventHandler OutputCleared;

        public MainViewModel()
        {
            _serialPortService = new SerialPortService();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            //System.Version is Major.Minor.Build.Revision, so the semver patch
            //number is Build - and this reads AssemblyVersion, not <Version>.
            Title = $"Serial Port Spy - v{v.Major}.{v.Minor}.{v.Build}";

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

            //Populates the status bar too, via the SelectedPortName setter -
            //hence _statusMessage and _baudRateText being assigned above first.
            Ports = new ObservableCollection<SerialPortInfo>();
            RefreshPortNames();

            _serialTimer = new DispatcherTimer();
            _serialTimer.Tick += OnSerialTimer_Tick;
            _serialTimer.Interval = TimeSpan.FromMilliseconds(1); //Query Serial Data every millisecond

            //Debounces device-change notifications (see NotifyDeviceChange).
            _deviceChangeTimer = new DispatcherTimer();
            _deviceChangeTimer.Tick += OnDeviceChangeTimer_Tick;
            _deviceChangeTimer.Interval = DEVICE_CHANGE_DEBOUNCE;

            //The IsPortOpen clause matters: once open the button means 'Close Port',
            //which must stay clickable no matter what is in the baud box.
            //RelayCommand routes CanExecuteChanged to CommandManager.RequerySuggested,
            //which WPF raises on keyboard input, so this re-evaluates as the user types.
            TogglePortCommand = new RelayCommand(
                TogglePort,
                () => !string.IsNullOrEmpty(SelectedPortName) && (IsPortOpen || TryGetBaudRate(out _)));

            //No CanExecute: clearing an already-empty log is harmless, and gating
            //on "is the document empty" would mean pulling view state back into
            //the ViewModel. Deliberately live while the port is open too - wiping
            //accumulated noise and then watching fresh output is the whole point.
            ClearOutputCommand = new RelayCommand(ClearOutput);
        }

        //-----------------------------------------------
        // Bindable properties
        //-----------------------------------------------

        public string Title { get; }

        /// <summary>
        /// The COM ports on offer, each carrying its device description. Bound
        /// with SelectedValuePath so the selection stays a plain port-name
        /// string (see SelectedPortName).
        /// </summary>
        public ObservableCollection<SerialPortInfo> Ports { get; }

        public ObservableCollection<string> BaudRates { get; }

        public Parity[] ParityOptions { get; }

        public StopBits[] StopBitsOptions { get; }

        public string[] DisplayDataOptions { get; }

        public ICommand TogglePortCommand { get; }

        public ICommand ClearOutputCommand { get; }

        /// <summary>
        /// The selected port's name, not the SerialPortInfo itself: the combo
        /// binds it through SelectedValuePath, which keeps every consumer here
        /// (open, close, the command predicate, the disconnect message) working
        /// with the plain string the SerialPort API actually wants.
        /// </summary>
        public string SelectedPortName
        {
            get => _selectedPortName;
            set
            {
                if (SetProperty(ref _selectedPortName, value))
                {
                    UpdateSelectedPortStatus();
                }
            }
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

        /// <summary>
        /// Called (debounced) from the view when Windows reports a device-tree
        /// change. Restarts the quiet-period countdown so a burst of
        /// WM_DEVICECHANGE messages collapses into a single refresh.
        /// </summary>
        public void NotifyDeviceChange()
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }

        private void OnDeviceChangeTimer_Tick(object sender, EventArgs e)
        {
            _deviceChangeTimer.Stop();
            RefreshPortNames();
        }

        /// <summary>
        /// Reconciles <see cref="Ports"/> with the ports Windows currently
        /// reports, in place: departed ports are removed and new arrivals inserted
        /// at their sorted position, leaving unchanged entries (and the ComboBox's
        /// bound selection) untouched. If the open port has vanished it is treated
        /// as a disconnect first. Also the startup populate - nothing is selected
        /// yet, so the selection rule below picks the first port as before.
        /// </summary>
        public void RefreshPortNames()
        {
            List<SerialPortInfo> current = EnumeratePorts();

            //The open port disappearing is a disconnect, not just a list edit.
            //The name check alone is not enough: some drivers keep the SERIALCOMM
            //registry entry (and so GetPortNames) alive while we hold the handle,
            //so also probe whether the device itself still responds.
            //Matched on PortName, not with Contains: the list holds descriptions
            //now, and a device that renamed itself must not read as a disconnect.
            if (IsPortOpen && _openPortName != null
                && (!current.Any(port => string.Equals(port.PortName, _openPortName, StringComparison.OrdinalIgnoreCase))
                    || !_serialPortService.IsDeviceResponsive()))
            {
                HandlePortLoss(_openPortName);

                //Closing our handle may have released the stale registry entry -
                //re-read so the dead port leaves the dropdown in this pass rather
                //than lingering until the next device change.
                current = EnumeratePorts();
            }

            //Both passes below compare by value, not reference - SerialPortInfo is
            //a record for exactly this reason. Rebuilding the list wholesale on
            //every tick would make the ComboBox selection flicker, and a port
            //whose description changed correctly reads as a remove then an insert.

            //Remove ports that are gone (iterate a copy - we mutate the collection).
            foreach (SerialPortInfo port in Ports.ToList())
            {
                if (!current.Contains(port))
                {
                    Ports.Remove(port);
                }
            }

            //Insert new arrivals at their sorted position so the list stays ordered.
            for (int i = 0; i < current.Count; i++)
            {
                if (i >= Ports.Count)
                {
                    Ports.Add(current[i]);
                }
                else if (Ports[i] != current[i])
                {
                    Ports.Insert(i, current[i]);
                }
            }

            EnsureSelectionValid();
        }

        /// <summary>
        /// Keeps the current selection if it survived the last list edit; otherwise
        /// falls back to the first available port (or null when none remain).
        /// </summary>
        private void EnsureSelectionValid()
        {
            if (SelectedPortName == null || FindPort(SelectedPortName) == null)
            {
                SelectedPortName = Ports.FirstOrDefault()?.PortName;
            }
        }

        /// <summary>
        /// The listed port of that name, or null if it isn't (or no longer is)
        /// on offer.
        /// </summary>
        private SerialPortInfo FindPort(string portName)
        {
            return Ports.FirstOrDefault(
                port => string.Equals(port.PortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The ports Windows currently reports, ordered numerically then by name,
        /// each labelled with its device description where Windows knows one.
        /// Distinct() because GetPortNames() reads the registry and is known to
        /// return the same port twice on some systems.
        ///
        /// The sort runs on the names, before they are paired with descriptions,
        /// so ordering stays by port number rather than by device name. Ports the
        /// description lookup doesn't cover keep a null description and render as
        /// the bare name - deliberately not an "Unknown" label, which is a
        /// long-standing complaint about the Arduino IDE's port menu.
        /// </summary>
        private List<SerialPortInfo> EnumeratePorts()
        {
            Dictionary<string, (string Description, bool IsUsb)> devices = _serialPortService.GetPortDescriptions();

            return _serialPortService.GetPortNames()
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(GetPortSortKey)
                                     .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                                     //Built from 'name', never from anything the lookup
                                     //returned: GetPortNames() stays authoritative for
                                     //which ports exist and what they are called.
                                     .Select(name => devices.TryGetValue(name, out var device)
                                                     ? new SerialPortInfo(name, device.Description, device.IsUsb)
                                                     : new SerialPortInfo(name, null, false))
                                     .ToList();
        }

        /// <summary>
        /// Sort key that orders COMn numerically (so COM2 precedes COM10). Names
        /// that aren't "COM&lt;number&gt;" sort last and fall back to text ordering.
        /// </summary>
        private static int GetPortSortKey(string portName)
        {
            if (portName != null
                && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(portName.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return number;
            }

            return int.MaxValue;
        }

        /// <summary>
        /// Called by the view when the window is closing.
        /// </summary>
        public void Shutdown()
        {
            _deviceChangeTimer.Stop();

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

            if (TryGetBaudRate(out _))
            {
                //Back to the idle message, which names the selected device when
                //Windows knows it and falls back to the ready text when it doesn't.
                UpdateSelectedPortStatus();
            }
            else
            {
                StatusMessage = INVALID_BAUD_MSG;
            }
        }

        /// <summary>
        /// Names the selected device in the status bar - the one place the full,
        /// untrimmed description is shown, since the closed combo has room only
        /// for the port name. Like UpdateBaudRateStatus it never sets IsError:
        /// picking a port is not a failed operation.
        ///
        /// It only ever displaces the neutral ready text. Everything else the
        /// status bar can be showing outranks it: an open confirmation, a red
        /// failure, or the invalid-baud hint.
        /// </summary>
        private void UpdateSelectedPortStatus()
        {
            if (IsPortOpen) return;             //"...successfully opened" stands
            if (IsError) return;                //A failure stands until the next attempt
            if (!TryGetBaudRate(out _)) return; //The baud hint is the more urgent message

            SerialPortInfo port = FindPort(SelectedPortName);

            StatusMessage = port == null || string.IsNullOrEmpty(port.Description)
                          ? READY_STATUS_MSG
                          : port.PortName + " - " + port.Description;
        }

        /// <summary>
        /// Blanks the log on request. Raises the same event the open path does,
        /// so there is one clear mechanism rather than two; the view decides how
        /// much of its own state to reset (see MainWindow.OnOutputCleared).
        ///
        /// Touches nothing else on purpose: the port stays open, the status bar
        /// keeps whatever it was showing, and no serial data is discarded - only
        /// what has already been rendered goes.
        /// </summary>
        private void ClearOutput()
        {
            OutputCleared?.Invoke(this, EventArgs.Empty);
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
                _openPortName = settings.PortName;
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
                if (!IsPortOpen) _openPortName = null;

                //After IsPortOpen drops, so the idle message can be written: names
                //the selected device again, or restores the ready text. No-ops on
                //the failure path above, where IsError guards the error message.
                UpdateSelectedPortStatus();
            }

            return true;
        }

        /// <summary>
        /// Handles the open port being physically removed mid-session (detected
        /// by a read throwing, by the stream closing itself, or by the refresh
        /// probe failing). Stops reading, closes what's left, and goes red.
        /// Guards on _openPortName - the ViewModel's own record - because on
        /// surprise removal the SerialPort stream often marks *itself* closed
        /// first, so the service's IsOpen cannot distinguish "already handled"
        /// from "needs handling". Nulling the field first makes it idempotent.
        /// </summary>
        private void HandlePortLoss(string portName)
        {
            if (_openPortName == null) return;
            _openPortName = null;

            _serialTimer.Stop();

            //Unconditional: on surprise removal the stream often marks itself
            //closed while the OS handle is STILL OPEN (IsOpen only reflects an
            //internal flag), and a leaked handle keeps the dead port listed in
            //GetPortNames() forever - the driver holds its SERIALCOMM registry
            //entry until the handle is freed. Close() handles every state
            //internally and never throws.
            _serialPortService.Close();

            //Not read back from the service: the port is gone regardless of
            //what a wedged handle claims. Re-enables config via IsConfigEnabled.
            IsPortOpen = false;

            //Drop the dead port from the dropdown here rather than leaving it to
            //the device-change refresh. That refresh is a one-shot fired from the
            //Windows broadcast, which arrives while our handle is still open - and
            //a driver keeps the port's SERIALCOMM registry entry (so GetPortNames
            //keeps listing it) until every handle is released. Once it has fired,
            //no second broadcast is coming, so a port missed on that pass would
            //linger in the list for the rest of the session.
            SerialPortInfo lost = FindPort(portName);
            if (lost != null) Ports.Remove(lost);
            EnsureSelectionValid();

            //Set last, after the list edit: EnsureSelectionValid moves the
            //selection to a surviving port, and that setter writes the newly
            //selected device's name to the status bar. Its IsError guard already
            //protects this message, but writing it afterwards means the disconnect
            //survives even if those guards are ever loosened.
            StatusMessage = portName + " was disconnected.";
            IsError = true;

            //And re-enumerate shortly, now that the handle is released: this both
            //confirms the removal and restores the port if it turns out Windows
            //still reports it (a probe can misjudge an unusual device).
            NotifyDeviceChange();
        }

        //------------------------------------------------------
        // EventHandlers
        //------------------------------------------------------

        private void OnSerialTimer_Tick(object sender, EventArgs e)
        {
            //The timer only runs while we believe a port is open, so the stream
            //reporting closed here means it noticed the device being surprise-
            //removed and shut itself - a disconnect, not a benign no-data tick.
            if (!_serialPortService.IsOpen)
            {
                HandlePortLoss(_openPortName);
                return;
            }

            string receivedString;

            //A port unplugged mid-read makes BytesToRead/Read throw. Catch it and
            //treat it as a disconnect rather than letting it surface as an unhandled
            //exception. A device-change refresh may reach the same conclusion first;
            //HandlePortLoss is idempotent, so whichever wins, the other no-ops.
            try
            {
                if (_serialPortService.BytesToRead < 1) return;

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
            }
            catch (Exception ex) when (ex is IOException
                                       || ex is InvalidOperationException
                                       || ex is UnauthorizedAccessException
                                       || ex is OperationCanceledException)
            {
                HandlePortLoss(_openPortName);
                return;
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
