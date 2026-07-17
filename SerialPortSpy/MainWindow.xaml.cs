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
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
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

        private readonly SolidColorBrush _pinkBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F02B63"));

        public MainWindow()
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::MainWindow()");

            _viewModel = new MainViewModel();
            _viewModel.DataReceived += OnDataReceived;
            _viewModel.OutputCleared += OnOutputCleared;
            DataContext = _viewModel;

            this.Loaded += OnLoaded;
            this.Closing += OnClosing;
            InitializeComponent();
        }

        //------------------------------------------------------
        // EventHandlers
        //------------------------------------------------------

        private void OnLoaded(Object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnLoaded()");

            this.RichTextBox_Data.Document.PageWidth = this.RichTextBox_Data.Width;

            if (_viewModel.PortNames.Count < 1)
            {
                MessageBox.Show("Have you connected your 'serial to USB' device\n(e.g. Keyspan Adapter, Arduino) ?\n\nThis program needs to find at least one COM Port in order to run.", "No COM Ports found!", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                //The user may read the message box, and decide to plug in a serial adapter at the last minute...
                //Check to see if this is the case, and we have a new COM Port.
                _viewModel.RefreshPortNames();

                if (_viewModel.PortNames.Count < 1)
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            Debug.WriteLine("[SerialPortSpy] MainWindow::OnClosing()");

            _viewModel.Shutdown();
        }

        private void OnOutputCleared(object sender, EventArgs e)
        {
            RichTextBox_Data.Document.Blocks.Clear();
        }

        private void OnDataReceived(object sender, string receivedString)
        {
            Run run = new Run(receivedString);

            //Switch the Text Color everytime we receive a new data 'chunk'
            if (_useAltColor)
            {
                run.Foreground = _pinkBrush;
            }
            else
            {
                run.Foreground = Brushes.SteelBlue;
            }

            _useAltColor = !_useAltColor;

            Paragraph paragraph = RichTextBox_Data.Document.Blocks.LastOrDefault() as Paragraph;
            if (paragraph == null)
            {
                paragraph = new Paragraph();
                RichTextBox_Data.Document.Blocks.Add(paragraph);
            }
            paragraph.Inlines.Add(run);

            RichTextBox_Data.ScrollToEnd();

            LimitDocumentSize();
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
