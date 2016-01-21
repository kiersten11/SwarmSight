﻿using System.Linq;
using System.Windows.Controls;
using Classes;
using SwarmVision.VideoPlayer;
using SwarmVision.Stats;
using OxyPlot.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwarmVision.Filters;
using SwarmVision.HeadPartsTracking;
using SwarmVision.UserControls;
using Frame = SwarmVision.Filters.Frame;
using Point = System.Windows.Point;

namespace SwarmVision
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private const string PlaySymbol = "4";
        private const string PauseSymbol = ";";

        private double _fullSizeWidth;
        private double _quality = 1;
        private List<Point> _activity = new List<Point>();
        private int _fpsStartFrame;
        private Stopwatch _fpsStopwatch = new Stopwatch();

        private ChartModel _chart;
        private VideoDecoder _decoder;
        private FrameComparer _comparer;
        private FrameRenderer _renderer;
        private readonly VideoProcessorBase _processor = new AntennaAndPERDetector();

        public MainWindow()
        {
            InitializeComponent();

            _fullSizeWidth = Width;
            
            ToggleCompare();
            SetupChart();
            SetupPlayer();

            Loaded += (sender, args) => UpdateComparerBounds();
            Closing += (sender, args) => Stop();

            Application.Current.Exit += (sender, args) => Stop();

#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException +=
                (sender, args) => { MessageBox.Show((args.ExceptionObject as Exception).Message); };
#endif
            //TESTs
            txtFileName.Text =
                @"Y:\Downloads\BeeVids\down.mp4";
                //@"Y:\Downloads\BeeVids\2015.8.15 Bee 5 Rose White Back.MP4";//done
                //@"Y:\Downloads\BeeVids\2015.8.15 Bee 1 Rose White Back.MP4";//done
                //@"Y:\Downloads\BeeVids\2015.8.13 Bee 9 Rose.MP4";//done
                //@"Y:\Downloads\BeeVids\2015.8.13 Bee 8 Rose.MP4";//DONE
                //@"Y:\Downloads\BeeVids\2015.8.13 Bee 4 Rose.MP4";//done
                //@"Y:\Downloads\BeeVids\2015.8.13 Bee 2 Rose.MP4";


            _comparer.MostRecentFrameIndex = 750;

            OnPlayClicked(null, null);
        }

        private void SetupChart()
        {
            _chart = new ChartModel();
            _activity = new List<Point>(100);

            chartPlaceholder.Children.Add(new PlotView()
                {
                    Model = _chart.MyModel,
                    Width = chartPlaceholder.Width,
                    Height = chartPlaceholder.Height,
                });

            chartA.UseCurrentClicked += OnUseCurrentClicked;
            chartB.UseCurrentClicked += OnUseCurrentClicked;
        }

        private void OnUseCurrentClicked(object sender, EventArgs e)
        {
            ((VideoActivityChart) sender).AddPointsToChart(_activity);

            ComputeComparisonStats();
        }

        private void ComputeComparisonStats()
        {
            if (TTest.Busy ||
                chartA.Activity == null || chartB.Activity == null ||
                chartA.Activity.Count <= 1 || chartB.Activity.Count <= 1)
                return;

            //Compute T-test
            var tTest = TTest.Perform
                (
                    chartA.Activity.Select(p => p.Y).ToList(),
                    chartB.Activity.Select(p => p.Y).ToList()
                );

            //Update table
            comparisonTable.lblAvgA.Content = tTest.TTest.FirstSeriesMean.ToString("N2");
            comparisonTable.lblAvgB.Content = tTest.TTest.SecondSeriesMean.ToString("N2");

            comparisonTable.lblNA.Content = tTest.FirstSeriesCount.ToString("N0");
            comparisonTable.lblNB.Content = tTest.SecondSeriesCount.ToString("N0");

            comparisonTable.lblStDevA.Content = tTest.FirstSeriesStandardDeviation.ToString("N2");
            comparisonTable.lblStDevB.Content = tTest.SecondSeriesStandardDeviation.ToString("N2");

            comparisonTable.lblAvgDiff.Content = (tTest.MeanDifference > 0 ? "+" : "") +
                                                 tTest.MeanDifference.ToString("N2");

            if (tTest.TTest.FirstSeriesMean != 0)
                comparisonTable.lblAvgPercent.Content = tTest.PercentMeanDifference.ToString("P2");
            else
                comparisonTable.lblAvgPercent.Content = "-";

            var pValue = tTest.TTest.ProbabilityTTwoTail;

            comparisonTable.lblPval.Content = pValue.ToString("N6");

            if (pValue < 0.001)
                comparisonTable.lblPvalStar.Content = "***";

            else if (pValue < 0.01)
                comparisonTable.lblPvalStar.Content = "**";

            else if (pValue < 0.05)
                comparisonTable.lblPvalStar.Content = "*";

            else
                comparisonTable.lblPvalStar.Content = "";

            //Update Chart
            comparisonChart.UpdateChart
                (
                    tTest.TTest.FirstSeriesMean, tTest.FirstSeries95ConfidenceBound,
                    tTest.TTest.SecondSeriesMean, tTest.SecondSeries95ConfidenceBound
                );
        }

        private void SetupPlayer()
        {
            _decoder = new VideoDecoder();
            _renderer = new FrameRenderer();
            _comparer = new FrameComparer(_decoder, _renderer);

            _decoder.Processor = _comparer.Processor = _processor;
            _comparer.FrameCompared += OnFrameCompared;
            _renderer.FrameReady += OnFrameReadyToRender;
            _comparer.Stopped += OnStopped;

            roi.RegionChanged += (sender, args) => UpdateComparerBounds();
        }

        private bool Open()
        {
            var videoFile = txtFileName.Text;

            if (!System.IO.File.Exists(videoFile))
            {
                MessageBox.Show("Please select a valid video file");
                return false;
            }

            _decoder.Open(videoFile);

            //Set chart range to number of frames in vid
            if (_comparer.MostRecentFrameIndex == -1)
            {
                _chart.SetRange(0, _decoder.VideoInfo.TotalFrames);
                _activity = new List<Point>(_decoder.VideoInfo.TotalFrames);
            }

            return true;
        }


        private void Play()
        {
            //Play
            if (btnPlayPause.Content.ToString() == PlaySymbol)
            {
                //Reset decoder
                if (_decoder != null)
                {
                    _decoder.Dispose();
                    _decoder = null;
                    _decoder = new VideoDecoder();
                    _decoder.PlayStartTimeInSec = 10;
                    _decoder.Processor = _processor;
                    _decoder.Open(txtFileName.Text);
                    _comparer.Decoder = _decoder;
                }

                //Can't change quality if playing
                sliderQuality.IsEnabled = false;

                //Clear chart points after the current position
                _chart.ClearAfter(_comparer.MostRecentFrameIndex);
                _activity.RemoveAll(p => p.X > _comparer.MostRecentFrameIndex);

                //Adjust for any quality changes, before starting again
                _decoder.PlayerOutputWidth = 204;//(int) (_decoder.VideoInfo.Width*_quality);
                _decoder.PlayerOutputHeight = 152;//(int) (_decoder.VideoInfo.Height*_quality);

                //Setup fps counter
                _fpsStartFrame = _comparer.MostRecentFrameIndex;
                _fpsStopwatch.Restart();

                //Play or resume
                _comparer.Start();

                btnPlayPause.Content = PauseSymbol;
            }
            else //Pause
            {
                Pause();
            }
        }

        private void Pause()
        {
            btnPlayPause.Content = PlaySymbol;
            _comparer.Pause();
            sliderQuality.IsEnabled = true;
        }

        private void Stop()
        {
            _comparer.Stop();
            _renderer.Stop();
            Reset();
        }

        private void SeekTo(double percentLocation)
        {
            _comparer.SeekTo(percentLocation);
        }

        private void OnFrameCompared(object sender, FrameComparisonArgs e)
        {
            if (e.Results == null || Application.Current == null)
                return;

            Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        _chart.AddPoint(e.Results.FrameIndex, e.Results.ChangedPixelsCount);
                    }
                    catch
                    {
                    }

                    _activity.Add(new Point(e.Results.FrameIndex, e.Results.ChangedPixelsCount));
                    lblChangedPixels.Content = string.Format("Changed Pixels: {0:n0}", e.Results.ChangedPixelsCount);
                    lblTime.Content = e.Results.FrameTime.ToString();
                });
        }

        private void OnStopped(object sender, EventArgs eventArgs)
        {
            _chart.Stop();

            Reset();
        }

        public static List<DateTime> fpsHist = new List<DateTime>();
        private static bool isDrawing = false;
        private static WriteableBitmap canvasBuffer;
        private static DateTime prevFrameTime = DateTime.Now;
        private void ShowFrame(Frame frame)
        {
            if (isDrawing)
                return;

            if (Application.Current == null)
                return;

            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                if (Application.Current == null)
                    return;

                isDrawing = true;

                if (canvasBuffer == null)
                {
                    canvasBuffer = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                    videoCanvas.Source = canvasBuffer;
                }

                if ((DateTime.Now - prevFrameTime).TotalMilliseconds > 1000/5.0)
                {
                    frame.CopyToWriteableBitmap(canvasBuffer);
                    prevFrameTime = DateTime.Now;
                }

                _sliderValueChangedByCode = true;
                sliderTime.Value = frame.FramePercentage*1000;

                //Compute FPS
                var now = DateTime.Now;
                fpsHist.Add(now);
                lblFPS.Content = string.Format("FPS: {0:n1}", fpsHist.Count(t => (now - t).TotalMilliseconds <= 1000));
                fpsHist.RemoveAll(t => (now - t).TotalMilliseconds > 1000);

                isDrawing = false;
            }));
        }

        private void ToggleCompare()
        {
            if (btnShowCompare.Content.ToString().Contains(">>"))
            {
                Width = _fullSizeWidth + 15;
                btnShowCompare.Content = btnShowCompare.Content.ToString().Replace(">>", "<<");
            }
            else
            {
                Width = borderComparison.Margin.Left + 15;
                btnShowCompare.Content = btnShowCompare.Content.ToString().Replace("<<", ">>");
            }

            CenterWindowOnScreen();
        }

        private void CenterWindowOnScreen()
        {
            double screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
            double screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
            double windowWidth = this.Width;
            double windowHeight = this.Height;
            this.Left = (screenWidth/2) - (windowWidth/2);
            this.Top = (screenHeight/2) - (windowHeight/2);
        }

        private void OnFrameReadyToRender(object sender, OnFrameReady e)
        {
            ShowFrame(e.Frame);
        }

        private void Reset()
        {
            Application.Current.Dispatcher.Invoke(() =>
                {
                    btnPlayPause.Content = PlaySymbol;

                    _sliderValueChangedByCode = true;
                    sliderTime.Value = 0;

                    sliderQuality.IsEnabled = true;
                });
        }

        private void txtFileName_LostFocus(object sender, RoutedEventArgs e)
        {
            Stop();
            Reset();
            Open();
        }

        private void OnBrowseClicked(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog();

            var result = ofd.ShowDialog();

            if (result == false)
                return;

            txtFileName.Text = ofd.FileName;

            Stop();
            Reset();
            Open();
        }

        private void thresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_comparer != null)
                _comparer.Threshold = (int) e.NewValue;

            if (lblThreshold != null)
                lblThreshold.Content = _comparer.Threshold;
        }

        private void OnPlayClicked(object sender, RoutedEventArgs e)
        {
            Play();
        }

        private void OnStopClicked(object sender, RoutedEventArgs e)
        {
            Stop();
        }

        private void chkShowMotion_Click(object sender, RoutedEventArgs e)
        {
            _renderer.ShowMotion = sliderContrast.IsEnabled = chkShowMotion.IsChecked.Value;
        }


        private void sliderTime_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            //Pause if slider is clicked
            if (!_comparer.IsPaused)
                Play(); //Pause
        }

        private void sliderTime_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            sliderTime_MouseDown(sender, e);
        }

        private bool _sliderValueChangedByCode;

        private void timeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderValueChangedByCode)
            {
                _sliderValueChangedByCode = false;
                return;
            }

            Pause();

            SeekTo(e.NewValue/1000.0);
        }

        private void btnShowCompare_Click(object sender, RoutedEventArgs e)
        {
            ToggleCompare();
        }

        private void sliderQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _quality = e.NewValue/100.0;

            if (lblQuality != null)
                lblQuality.Content = string.Format("{0:n0}%", _quality*100.0);
        }

        private void contrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_renderer != null)
                _renderer.ShadeRadius = (int) e.NewValue;

            if (lblContrast != null)
            {
                lblContrast.Content = _renderer.ShadeRadius + "X";
            }
        }

        private void btnSaveActivity_Click(object sender, RoutedEventArgs e)
        {
            new Thread(SaveCSV) {IsBackground = true}.Start(txtFileName.Text);
        }

        private void SaveCSV(object videoFileName)
        {
            Dispatcher.Invoke(() => btnSaveActivity.Content = "Saving...");

            var fileInfo = new FileInfo(videoFileName.ToString());

            using (var writer = new StreamWriter(fileInfo.FullName + ".csv", false))
            {
                writer.WriteLine("Frame, Changed Pixels");

                _activity.ForEach(a => writer.WriteLine("{0}, {1}", a.X + 1, a.Y));

                writer.Flush();
            }

            Dispatcher.InvokeAsync(() => btnSaveActivity.Content = "Saved!");

            new Thread(() =>
                {
                    Thread.Sleep(2000);

                    Dispatcher.InvokeAsync(() => { btnSaveActivity.Content = "Save Activity Data"; });
                })
                {
                    IsBackground = true
                }
                .Start();
        }

        private void btnComputeStats_Click(object sender, RoutedEventArgs e)
        {
            ComputeComparisonStats();
        }

        private void btnROI_Click(object sender, RoutedEventArgs e)
        {
            if (roi.Visibility == Visibility.Visible)
            {
                roi.Visibility = Visibility.Hidden;
                btnROI.Content = "Add Region of Interest";
            }
            else //Hidden
            {
                roi.Visibility = Visibility.Visible;
                btnROI.Content = "Remove Region of Interest";
            }

            UpdateComparerBounds();
        }

        private void UpdateComparerBounds()
        {
            if (roi.Visibility == Visibility.Visible)
            {
                _comparer.SetBounds(roi.LeftPercent, roi.TopPercent, roi.RightPercent, roi.BottomPercent);

                txtLeft.Text = roi.LeftPercent.ToString("P2");
                txtRight.Text = roi.RightPercent.ToString("P2");
                txtTop.Text = roi.TopPercent.ToString("P2");
                txtBottom.Text = roi.BottomPercent.ToString("P2");
            }

            else
            {
                _comparer.SetBounds(0, 0, 1, 1);

                txtLeft.Text = 0.ToString("P2");
                txtRight.Text = 0.ToString("P2");
                txtTop.Text = 1.ToString("P2");
                txtBottom.Text = 1.ToString("P2");
            }
        }

        private void ddlParams_Loaded(object sender, RoutedEventArgs e)
        {
            var configClass = typeof (AntennaAndPERDetector.Config);
            var fields = configClass.GetFields().Select(f => f.Name).ToList();

            ddlParams.ItemsSource = fields;
            ddlParams.SelectedIndex = 0;
        }

        private void ddlParams_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            txtValue.Text = typeof(AntennaAndPERDetector.Config).GetField(ddlParams.SelectedItem as string).GetValue(null).ToString();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var field = typeof (AntennaAndPERDetector.Config).GetField(ddlParams.SelectedItem as string);
            var newValue = field.FieldType.GetMethod("Parse", new[] {typeof(string)}).Invoke(null, new object[] {txtValue.Text});
            field.SetValue(null, newValue);

            Stop();
            Play();
        }
    }
}