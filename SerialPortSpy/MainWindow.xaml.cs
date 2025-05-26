/*
 * Serial Port Spy 
 * ---------------
 * 
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */


using System;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.IO.Ports;
using System.Diagnostics;
using System.Windows.Threading;
using System.ComponentModel;



namespace SerialPortSpy
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        MessageBoxResult _messageBoxResult;

        // COM Port vars
        private SerialPort _serialPort;
        private string[] _serialPortNames;
        private int _displayDataAs;

        private bool _useAltColor;

        private string[] _baudRates;
        private Parity _selectedParity;
        private StopBits _selectedStopBits;
        private string[] _displayDataOptions;

        private Action _receivedDataAction;

        private DispatcherTimer  _serialTimer;

        private const string READY_STATUS_MSG = "Click 'Open Port' to start reading incomming data.";


        //-----------------------------------------------
        // Methods
        //-----------------------------------------------
        public MainWindow()
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::MainWindow()");
            this.Loaded += OnLoaded;
            this.Closing += OnClosing;
            InitializeComponent();
        }

        private void FindCOMPorts()
        {
            _serialPortNames = SerialPort.GetPortNames();
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnLoaded() - Num Serial Ports: " + _serialPortNames.Length);

            if (_serialPortNames.Length < 1)
            {
                _messageBoxResult = MessageBox.Show("Have you connected your 'serial to USB' device\n(e.g. Keyspan Adapter, Arduino) ?\n\nThis program needs to find at least one COM Port in order to run.", "No COM Ports found!", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                //The user may read the message box, and decide to plug in a serial adapter at the last minute...
                //Check to see if this is the case, and we have a new COM Port.
                _serialPortNames = SerialPort.GetPortNames();

                if (_serialPortNames.Length < 1)
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
        }

        private void InitTimer()
        {
            _serialTimer = new DispatcherTimer();
            _serialTimer.Tick += new EventHandler(OnSerialTimer_Tick);
            _serialTimer.Interval = new TimeSpan(0, 0, 0, 0, 1); //Query Serial Data every millisecond
        }


        private void PopulateGUI()
        {
            _useAltColor = false;

            //Set up text output
            this.TextBlock_Data.FontFamily = new FontFamily("Courier New");
            this.TextBlock_Data.FontSize = 12;
            this.TextBlock_Data.TextWrapping = TextWrapping.Wrap;
            this.TextBlock_Data.Foreground = Brushes.SteelBlue;

           
            //-- Populate 'COM Port Names' Combobox --
            foreach (string name in _serialPortNames)
            {
                this.ComboBox_COMPortName.Items.Add(name);
            }

            this.ComboBox_COMPortName.SelectedIndex = 0;
            this.ComboBox_COMPortName.IsEnabled = true;


            //-- Populate 'Baud Rates' ComboBox --
            _baudRates = new string[] { "300", "600", "1200", "2400", "4800", "9600", "14400", "19200", "28800", "38400", "57600", "115200" };

            foreach (string rate in _baudRates)
            {
                this.ComboBox_BaudRate.Items.Add(rate);
            }

            this.ComboBox_BaudRate.SelectedIndex = 5;
            this.ComboBox_BaudRate.IsEnabled = true;

            //-- Populate 'Parity' ComboBox --
            this.ComboBox_Parity.ItemsSource = Enum.GetNames(typeof(Parity));
            this.ComboBox_Parity.SelectedIndex = 0;
            this.ComboBox_Parity.IsEnabled = true;

            //-- Populate 'StopBit' ComboBox --
            this.ComboBox_StopBits.ItemsSource = Enum.GetNames(typeof(StopBits));
            this.ComboBox_StopBits.SelectedIndex = 1;
            this.ComboBox_StopBits.IsEnabled = true;

            //-- Populate 'Display Data As' ComboBox --
            _displayDataOptions = new string[] { "Decimal", "ASCII" };

            foreach (string displayData in _displayDataOptions)
            {
                this.ComboBox_DisplayDataAs.Items.Add(displayData);
            }

            this.ComboBox_DisplayDataAs.SelectedIndex = 0;
            this.ComboBox_DisplayDataAs.IsEnabled = true;

           this.Button_TogglePort.IsEnabled = true;
        }

        private void InitSerialPort()
        {
            this._serialPort = new SerialPort();

            SetSerialPortProperties();
        }

        private void SetSerialPortProperties()
        {
            this._serialPort.PortName = this.ComboBox_COMPortName.Text;
            this._serialPort.BaudRate = Convert.ToInt32(this.ComboBox_BaudRate.Text);

            Enum.TryParse<Parity>(ComboBox_Parity.SelectedValue.ToString(), out _selectedParity);
            this._serialPort.Parity = _selectedParity;

            Enum.TryParse<StopBits>(ComboBox_StopBits.SelectedValue.ToString(), out _selectedStopBits);
            this._serialPort.StopBits = _selectedStopBits;

            //Options not exposed in UI
            this._serialPort.Handshake = Handshake.None;
            this._serialPort.DataBits = 8;
            this._serialPort.Encoding = Encoding.Default;

            _displayDataAs = ComboBox_DisplayDataAs.SelectedIndex;
        }

        private bool OpenPort()
        {
            try
            {
                this.Label_Status.Content = "Trying to open " + this.ComboBox_COMPortName.Text + " ...";
                SetSerialPortProperties();

                _serialPort.Open();
                _serialPort.BaseStream.Flush();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                this.TextBlock_Data.Text = "";

                this._serialTimer.Start();
            }
            catch(Exception error)
            {
                string errorDescription = error.ToString();
                errorDescription = errorDescription.Substring(0, errorDescription.IndexOf("\n"));
                errorDescription = errorDescription.Substring(errorDescription.IndexOf(":") + 2);
                this.Label_Status.Content = errorDescription;
                return false;
            }

            return true;
        }

        private bool ClosePort()
        {
            try
            {
                this.Label_Status.Content = "Trying to close " + this.ComboBox_COMPortName.Text + " ...";

                //this._serialPort.DataReceived -= OnSerialDataReceived;
                
                this._serialPort.BaseStream.Flush();
                this._serialPort.DiscardInBuffer();
                this._serialPort.DiscardOutBuffer();

                this._serialTimer.Stop();
                this._serialPort.Close();
            }
            catch(Exception error)
            {
                string errorDescription = error.ToString();
                errorDescription = errorDescription.Substring(0, errorDescription.IndexOf("\n"));
                errorDescription = errorDescription.Substring(errorDescription.IndexOf(":") + 2);
                this.Label_Status.Content = errorDescription;
                return false;
            }

            return true;
        }

        //------------------------------------------------------
        // EventHandlers
        //------------------------------------------------------


        private void OnLoaded(Object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnLoaded()");

            //_receivedDataAction = new Action(DisplaySerialData);
            _receivedDataAction = new Action(AddReceive);

            InitTimer();
            FindCOMPorts();
            PopulateGUI();
            InitSerialPort();

            //We are now ready for the user.
            Label_Status.Content = READY_STATUS_MSG;
        }


        private void OnSerialTimer_Tick(object sender, EventArgs e)
        {
            this.Dispatcher.BeginInvoke(_receivedDataAction, DispatcherPriority.Send);  
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            this.Dispatcher.BeginInvoke(_receivedDataAction, DispatcherPriority.Send);      
        }

        private void AddReceive()
        {
            if (_serialPort.BytesToRead < 1) return;

            string receivedString; 

            if (_displayDataAs == 0)
            {
                byte[] data = new byte[_serialPort.BytesToRead];
                _serialPort.Read(data, 0, data.Length);
                receivedString = "";

                foreach (char b in data)
                {
                    receivedString += ((int)b).ToString("D3");
                    receivedString += " ";
                }

            }
            else
            {
                receivedString = this._serialPort.ReadExisting();
            }

            TextRange textRange = new TextRange(TextBlock_Data.ContentEnd, TextBlock_Data.ContentEnd);
            textRange.Text = receivedString;

            //Switch the Text Color everytime we receive a new data 'chunk'
            if (_useAltColor)
            {
                textRange.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.IndianRed);
            }
            else
            {
                textRange.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.SteelBlue);
            }

            _useAltColor = !_useAltColor;

            this.ScrollViewer_Data.UpdateLayout();
            this.ScrollViewer_Data.ScrollToVerticalOffset(TextBlock_Data.ActualHeight);

                        
            if (TextBlock_Data.Text.Length > 12000)
            {
                TextBlock_Data.Text = TextBlock_Data.Text.Remove(0, 6000);
            }

        }


        private void OnClosing(object sender, CancelEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnClosing()");

            if (_serialPort.IsOpen)
            {
                ClosePort();
            }

        }




        private void Button_TogglePort_Click(object sender, RoutedEventArgs e)
        {
            if (this._serialPort.IsOpen)
            { //Let's close the port
                ClosePort();

                if (!_serialPort.IsOpen)
                { //Serial Port closed successfully :)
                    this.Button_TogglePort.Content = "Open Port";
                    this.Label_Status.Content = READY_STATUS_MSG;
                    this.ComboBox_COMPortName.IsEnabled = true;
                    this.ComboBox_BaudRate.IsEnabled = true;
                    this.ComboBox_Parity.IsEnabled = true;
                    this.ComboBox_StopBits.IsEnabled = true;
                    this.ComboBox_DisplayDataAs.IsEnabled = true;
                }
            }
            else //Serial port is currently closed, let's open it
            {
                this.TextBlock_Data.Text = "";

                OpenPort();

                if (_serialPort.IsOpen)
                {
                    this.Button_TogglePort.Content = "Close Port";
                    this.Label_Status.Content = this._serialPort.PortName + " successfully opened. Reading incoming bytes at " + _serialPort.BaudRate + " bps.";

                    this.ComboBox_COMPortName.IsEnabled = false;
                    this.ComboBox_BaudRate.IsEnabled = false;
                    this.ComboBox_Parity.IsEnabled = false;
                    this.ComboBox_StopBits.IsEnabled = false;
                    this.ComboBox_DisplayDataAs.IsEnabled = false;
                }
            }
        }

    }
}
