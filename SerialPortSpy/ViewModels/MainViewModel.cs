/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Collections.ObjectModel;
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
        private const string READY_STATUS_MSG = "Click 'Open Port' to start reading incomming data.";

        private readonly SerialPortService _serialPortService;
        private readonly DispatcherTimer _serialTimer;

        private string _selectedPortName;
        private string _baudRateText;
        private Parity _selectedParity;
        private StopBits _selectedStopBits;
        private string _selectedDisplayDataOption;
        private string _statusMessage;
        private bool _isPortOpen;

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

            BaudRates = new ObservableCollection<string>(
                new[] { "300", "600", "1200", "2400", "4800", "9600", "14400", "19200", "28800", "38400", "57600", "115200" });
            ParityOptions = Enum.GetValues(typeof(Parity)).Cast<Parity>().ToArray();
            StopBitsOptions = Enum.GetValues(typeof(StopBits)).Cast<StopBits>().ToArray();
            DisplayDataOptions = new[] { "Decimal", "ASCII" };

            _baudRateText = "9600";
            _selectedParity = Parity.None;
            _selectedStopBits = StopBits.One;
            _selectedDisplayDataOption = DisplayDataOptions[0];
            _statusMessage = READY_STATUS_MSG;

            PortNames = new ObservableCollection<string>();
            RefreshPortNames();

            _serialTimer = new DispatcherTimer();
            _serialTimer.Tick += OnSerialTimer_Tick;
            _serialTimer.Interval = TimeSpan.FromMilliseconds(1); //Query Serial Data every millisecond

            TogglePortCommand = new RelayCommand(TogglePort, () => !string.IsNullOrEmpty(SelectedPortName));
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
            set => SetProperty(ref _baudRateText, value);
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
                StatusMessage = "Trying to open " + SelectedPortName + " ...";

                var settings = new SerialPortSettings
                {
                    PortName = SelectedPortName,
                    BaudRate = Convert.ToInt32(BaudRateText),
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
                StatusMessage = "Trying to close " + SelectedPortName + " ...";

                _serialTimer.Stop();
                _serialPortService.Close();

                StatusMessage = READY_STATUS_MSG;
            }
            catch (Exception error)
            {
                StatusMessage = error.Message;
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

            if (SelectedDisplayDataOption == "Decimal")
            {
                byte[] data = _serialPortService.ReadAvailableBytes();
                var builder = new StringBuilder(data.Length * 4);

                foreach (byte b in data)
                {
                    builder.Append(b.ToString("D3"));
                    builder.Append(' ');
                }

                receivedString = builder.ToString();
            }
            else
            {
                receivedString = _serialPortService.ReadExisting();
            }

            DataReceived?.Invoke(this, receivedString);
        }
    }
}
