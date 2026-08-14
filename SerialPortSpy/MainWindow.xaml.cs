/*
 * Serial Port Spy
 * ---------------
 *
 * Website: https://ludicworlds.com
 * GitHub:  https://github.com/LudicWorlds
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SerialPortSpy.ViewModels;

namespace SerialPortSpy
{
    /// <summary>
    /// Code-behind for MainWindow. Contains only view logic: rendering received
    /// data chunks into the RichTextBox (FlowDocument content is not bindable)
    /// and the startup "no COM ports" dialog. All other logic is in MainViewModel.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        private bool _useAltColor;

        //True when the previous chunk ended on a CR. The 1ms poll can split a
        //CRLF across two reads, and without this the trailing LF would be treated
        //as a second line break and emit a blank line.
        private bool _pendingCr;

        //Received chunks alternate between these two theme brushes. Both are warm
        //tones ~12 deg apart in hue, so they are separated mainly by lightness
        //(84% vs 73%) rather than by hue.
        private readonly Brush _chunkBrushYellow;
        private readonly Brush _chunkBrushOrange;

        //The device-change message hook, kept so it can be removed on close.
        private HwndSource _hwndSource;

        //How many bytes the plotter shows at once. About 1s of data at 9600
        //baud, ~87ms at 115200 - sized for the Arduino rates this app targets.
        //The obvious future setting if anyone asks for a wider window.
        private const int PLOT_POINT_COUNT = 1000;

        //The scrolling fixed-window trace. Recreated (not cleared) on a log
        //clear - see ResetPlot().
        private ScottPlot.Plottables.DataStreamer _plotStreamer;

        //Renders the plot at ~30fps. The serial timer ticks every 1ms, and
        //rendering on each data tick would burn the UI thread for nothing - so
        //data is *added* on the serial tick and *drawn* on this one, and only
        //while the plotter tab is actually in front.
        private DispatcherTimer _plotRenderTimer;

        public MainWindow()
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::MainWindow()");

            _viewModel = new MainViewModel();
            _viewModel.DataReceived += OnDataReceived;
            _viewModel.BytesReceived += OnBytesReceived;
            _viewModel.OutputCleared += OnOutputCleared;
            DataContext = _viewModel;

            this.Loaded += OnLoaded;
            this.Closing += OnClosing;
            InitializeComponent();

            //FindResource (rather than the Resources indexer) so a missing
            //theme key fails fast instead of silently rendering default colors.
            _chunkBrushYellow = (Brush)FindResource("Brush.YellowLight");
            _chunkBrushOrange = (Brush)FindResource("Brush.OrangePale");

            ConfigurePlot();

            _plotRenderTimer = new DispatcherTimer();
            _plotRenderTimer.Tick += OnPlotRenderTimer_Tick;
            _plotRenderTimer.Interval = TimeSpan.FromMilliseconds(33); //~30fps
            _plotRenderTimer.Start();
        }

        /// <summary>
        /// One-time plot setup. In code rather than XAML because ScottPlot is
        /// configured through its API, not through WPF properties - the same
        /// reason the log renders from code-behind.
        /// </summary>
        private void ConfigurePlot()
        {
            var plot = PlotView.Plot;

            //Hex duplicates of Theme.xaml tokens: ScottPlot takes raw colours,
            //not WPF brushes. If the theme is retuned, retune these with it.
            plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1C1C1C"); //Brush.BgElevated
            plot.DataBackground.Color = ScottPlot.Color.FromHex("#1C1C1C");   //Brush.BgElevated
            plot.Axes.Color(ScottPlot.Color.FromHex("#9A9A9A"));              //Brush.MutedStrong
            plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2A2A2A");    //Brush.Border

            //Mono for the byte values on the Y axis (numbers are data). The X
            //axis is hidden entirely: its unit is "sample index within the
            //window", which would read as a measurement and mean nothing.
            plot.Axes.Left.TickLabelStyle.FontName = "Cascadia Code";
            plot.Axes.Left.TickLabelStyle.FontSize = 12;
            plot.Axes.Bottom.TickLabelStyle.IsVisible = false;

            _plotStreamer = CreateStreamer(plot);

            //Byte-aligned graticule: 8 divisions of 32, the oscilloscope
            //convention, with the top tick at 255 because that is the true
            //byte ceiling (the final 31-wide gap is the honest price of the
            //maximum being 255, not 256). Manual ticks are position-fixed -
            //resizing the plot never re-picks them, unlike the automatic
            //generator's decimal steps (0/50/100), which divide a byte
            //meaninglessly. The grid draws at these same positions.
            var ticks = new ScottPlot.TickGenerators.NumericManual();
            for (int v = 0; v <= 224; v += 32)
            {
                ticks.AddMajor(v, v.ToString(CultureInfo.InvariantCulture));
            }
            ticks.AddMajor(255, "255");
            plot.Axes.Left.TickGenerator = ticks;

            //Both axes are fixed, so nothing ever rescales under the user's
            //eyes: scroll view keeps X at [0, count] with the newest sample at
            //the right, and Y is exactly the byte range - 0 flush with the
            //bottom frame, 255 with the top. A flat run of 0s or 255s
            //therefore draws ON the frame line, as a scope trace does at the
            //graticule edge; that is the accepted price of flush limits.
            plot.Axes.SetLimits(0, PLOT_POINT_COUNT, 0, 255);

            //Pan/zoom would fight the fixed limits - this is a live scope view,
            //not an explorable chart.
            PlotView.UserInputProcessor.IsEnabled = false;
        }

        /// <summary>
        /// Builds the trace. Separate from ConfigurePlot because ResetPlot
        /// replaces the streamer wholesale - recreating is guaranteed to blank
        /// the fixed-length buffer, where clearing it is API-version dependent.
        /// </summary>
        private static ScottPlot.Plottables.DataStreamer CreateStreamer(ScottPlot.Plot plot)
        {
            ScottPlot.Plottables.DataStreamer streamer = plot.Add.DataStreamer(PLOT_POINT_COUNT);

            streamer.LineStyle.Color = ScottPlot.Color.FromHex("#FFBD74"); //Brush.OrangePale - a data colour
            streamer.LineStyle.Width = 1;

            //Scroll, not wipe: the trace slides left as new data enters at the
            //right, like the Arduino IDE's own Serial Plotter.
            streamer.ViewScrollLeft();

            //The axes are pinned in ConfigurePlot; the streamer must not manage
            //them or it would re-fit Y to the data on every render.
            streamer.ManageAxisLimits = false;

            return streamer;
        }

        /// <summary>
        /// Ask DWM for the native dark title bar as soon as the HWND exists,
        /// before first paint, so there is no light-to-dark flash.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Interop.DarkTitleBar.Apply(this);

            //Listen for USB-serial adapters being plugged in or unplugged so the
            //COM Port list stays live. The HWND exists by now, so the hook takes.
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);
        }

        /// <summary>
        /// Observes WM_DEVICECHANGE and forwards the coarse "a device changed"
        /// cue to the ViewModel, which debounces and refreshes the port list.
        /// Observe-only: never sets handled, so normal message routing continues.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (Interop.DeviceNotifications.IsPortListChange(msg, wParam))
            {
                _viewModel.NotifyDeviceChange();
            }

            return IntPtr.Zero;
        }

        //------------------------------------------------------
        // EventHandlers
        //------------------------------------------------------

        private void OnLoaded(Object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnLoaded()");

            if (_viewModel.Ports.Count < 1)
            {
                MessageBox.Show("Have you connected your 'serial to USB' device\n(e.g. Keyspan Adapter, Arduino) ?\n\nThis program needs to find at least one COM Port in order to run.", "No COM Ports found!", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                //The user may read the message box, and decide to plug in a serial adapter at the last minute...
                //Check to see if this is the case, and we have a new COM Port.
                _viewModel.RefreshPortNames();

                if (_viewModel.Ports.Count < 1)
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnClosing()");

            _hwndSource?.RemoveHook(WndProc);
            _plotRenderTimer.Stop();

            _viewModel.Shutdown();
        }

        /// <summary>
        /// The plotter's feed: every received byte becomes one point. Runs on
        /// every data tick whichever tab is in front - Add() is O(1) into a
        /// fixed buffer, and staying fed means switching to the plotter never
        /// shows a gap in the trace. Drawing happens on the render timer.
        /// </summary>
        private void OnBytesReceived(object sender, byte[] data)
        {
            foreach (byte b in data)
            {
                _plotStreamer.Add(b);
            }
        }

        private void OnPlotRenderTimer_Tick(object sender, EventArgs e)
        {
            //Idle while the log tab is in front - no point drawing offscreen -
            //and skip frames where nothing arrived.
            if (!PlotterTab.IsSelected) return;
            if (!_plotStreamer.HasNewData) return;

            PlotView.Refresh();
        }

        private void OnOutputTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //Selector events bubble; only act on the TabControl's own.
            if (!ReferenceEquals(e.OriginalSource, OutputTabs)) return;

            //Catch up immediately on switching in, rather than showing a stale
            //frame until the render timer's next tick.
            if (PlotterTab.IsSelected)
            {
                PlotView.Refresh();
            }
        }

        /// <summary>
        /// Blanks the trace by replacing the streamer: its buffer is fixed
        /// length and pre-filled, so recreation is the reliable way to empty it.
        /// The fresh trace draws flat at 0 until data arrives - scope-like, and
        /// honest about what an idle line reads as.
        /// </summary>
        private void ResetPlot()
        {
            PlotView.Plot.Remove(_plotStreamer);
            _plotStreamer = CreateStreamer(PlotView.Plot);
            PlotView.Refresh();
        }

        /// <summary>
        /// Blanks the log. Serves two callers - a session starting, and the user
        /// clicking Clear mid-stream - which need different amounts of reset.
        /// </summary>
        private void OnOutputCleared(object sender, EventArgs e)
        {
            //Each fresh log starts on the yellow brush. Safe either way: which
            //colour a chunk gets is cosmetic.
            _useAltColor = false;

            //Only when no session is running. Mid-stream this flag must survive:
            //a CR whose LF has not arrived yet still has to pair up, or the LF
            //lands as a second break and the cleared log opens on a blank line.
            //IsPortOpen is the reliable test here - the open path raises this
            //event before OpenPort(), while the flag is still false.
            if (!_viewModel.IsPortOpen) _pendingCr = false;

            RichTextBox_Data.Document.Blocks.Clear();

            //One byte stream, one Clear: the plot blanks alongside the log,
            //whether this fired from the Clear command or a session starting.
            ResetPlot();
        }

        private void OnDataReceived(object sender, string receivedString)
        {
            //One colour per received chunk, even when the chunk spans several lines
            Brush chunkBrush = _useAltColor ? _chunkBrushOrange : _chunkBrushYellow;
            _useAltColor = !_useAltColor;

            //A Run's \r\n is collapsible whitespace to the FlowDocument layout, not
            //a line break - only a paragraph boundary breaks the line. So split the
            //chunk here and start a new Paragraph at each newline.
            int segmentStart = 0;

            for (int i = 0; i < receivedString.Length; i++)
            {
                char c = receivedString[i];

                if (c != '\r' && c != '\n') continue;

                //The LF half of a CRLF that straddled two reads - already broken
                if (c == '\n' && i == 0 && _pendingCr)
                {
                    segmentStart = 1;
                    _pendingCr = false;
                    continue;
                }

                AppendText(receivedString.Substring(segmentStart, i - segmentStart), chunkBrush);
                StartNewLine();

                //Consume the LF of a CRLF pair so it doesn't break the line twice
                if (c == '\r' && i + 1 < receivedString.Length && receivedString[i + 1] == '\n')
                {
                    i++;
                }

                segmentStart = i + 1;
            }

            AppendText(receivedString.Substring(segmentStart), chunkBrush);

            //Only a trailing CR can pair with an LF in the next chunk. Leave the
            //flag alone on an empty chunk - DecodeReceivedText() returns "" when
            //the chunk holds a partial UTF-8 sequence, and clearing here would
            //strand a CR whose LF has not arrived yet.
            if (receivedString.Length > 0)
            {
                _pendingCr = receivedString[receivedString.Length - 1] == '\r';
            }

            RichTextBox_Data.ScrollToEnd();

            LimitDocumentSize();
        }

        /// <summary>
        /// Appends one newline-free segment to the last paragraph.
        /// </summary>
        private void AppendText(string text, Brush brush)
        {
            if (text.Length < 1) return;

            Paragraph paragraph = RichTextBox_Data.Document.Blocks.LastOrDefault() as Paragraph;
            if (paragraph == null)
            {
                paragraph = NewParagraph();
                RichTextBox_Data.Document.Blocks.Add(paragraph);
            }
            else
            {
                //RichTextBox re-inserts a default-margin paragraph after a Clear()
                paragraph.Margin = new Thickness(0);
            }

            paragraph.Inlines.Add(new Run(MakeControlsVisible(text)) { Foreground = brush });
        }

        private void StartNewLine()
        {
            RichTextBox_Data.Document.Blocks.Add(NewParagraph());
        }

        /// <summary>
        /// Zero margin: WPF's default paragraph spacing would put a gap between
        /// every line of the log.
        /// </summary>
        private static Paragraph NewParagraph()
        {
            return new Paragraph { Margin = new Thickness(0) };
        }

        /// <summary>
        /// Swaps non-printing C0 controls for their Unicode Control Pictures
        /// (ESC becomes U+241B) so they are visible rather than painting nothing.
        /// Tab is left alone; CR/LF never reach here - the line splitter ate them.
        /// Note this changes what Ctrl+C copies; Hex mode is the byte-faithful view.
        /// </summary>
        private static string MakeControlsVisible(string text)
        {
            char[] buffer = null;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c > 0x1F && c != 0x7F) continue;
                if (c == '\t') continue;

                buffer = buffer ?? text.ToCharArray();

                //U+2400 block runs NUL..US in byte order, then U+2421 for DEL
                buffer[i] = c == 0x7F ? '␡' : (char)(0x2400 + c);
            }

            return buffer == null ? text : new string(buffer);
        }

        private void LimitDocumentSize()
        {
            // Limit document size by removing oldest content while preserving formatting
            var doc = RichTextBox_Data.Document;
            var textRange = new TextRange(doc.ContentStart, doc.ContentEnd);
            if (textRange.Text.Length > 12000)
            {
                // Remove content from the beginning while preserving paragraph structure
                var blocks = doc.Blocks.ToList();
                int removedLength = 0;
                int targetRemoval = 6000;
                var blocksToRemove = new List<Block>();

                foreach (var block in blocks)
                {
                    if (removedLength >= targetRemoval) break;

                    if (block is Paragraph para)
                    {
                        var inlines = para.Inlines.ToList();
                        var inlinesToRemove = new List<Inline>();

                        foreach (var inline in inlines)
                        {
                            if (removedLength >= targetRemoval) break;

                            if (inline is Run inlineRun)
                            {
                                int runLength = inlineRun.Text.Length;
                                if (removedLength + runLength <= targetRemoval)
                                {
                                    inlinesToRemove.Add(inlineRun);
                                    removedLength += runLength;
                                }
                                else
                                {
                                    // Partially remove text from this run
                                    int charsToRemove = targetRemoval - removedLength;
                                    inlineRun.Text = inlineRun.Text.Substring(charsToRemove);
                                    removedLength = targetRemoval;
                                    break;
                                }
                            }
                        }

                        // Remove the inlines we marked for removal
                        foreach (var inlineToRemove in inlinesToRemove)
                        {
                            para.Inlines.Remove(inlineToRemove);
                        }

                        // If paragraph is now empty, mark it for removal
                        if (!para.Inlines.Any())
                        {
                            blocksToRemove.Add(para);

                            // TextRange.Text counts each paragraph break as \r\n, so
                            // without this the loop under-trims: the document would
                            // creep past the cap and re-run this walk on every chunk.
                            removedLength += 2;
                        }
                    }
                }

                // Remove empty blocks
                foreach (var blockToRemove in blocksToRemove)
                {
                    doc.Blocks.Remove(blockToRemove);
                }
            }
        }
    }
}
